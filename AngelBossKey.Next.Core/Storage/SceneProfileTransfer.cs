using AngelBossKey.Next.Core.Models;
using System.Text.Json;

namespace AngelBossKey.Next.Core.Storage;

public static class SceneProfileTransfer
{
    private const int ExportSchemaVersion = 1;
    public const int MaximumImportBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string Export(SceneProfile scene) => JsonSerializer.Serialize(
        new SceneExportPackage { Scene = scene },
        Options);

    public static SceneProfile Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("场景文件为空。");
        }
        if (json.Length > MaximumImportBytes)
        {
            throw new InvalidDataException("场景文件超过 1 MB，已拒绝导入。");
        }

        SceneExportPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<SceneExportPackage>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("场景文件不是有效的 JSON。", exception);
        }

        if (package is not { SchemaVersion: ExportSchemaVersion, Scene: not null })
        {
            throw new InvalidDataException("场景文件版本不受支持或缺少场景数据。");
        }

        var source = package.Scene;
        var targets = (source.Targets ?? [])
            .Where(target => target is not null &&
                !string.IsNullOrWhiteSpace(target.DisplayName) &&
                IsLocalPath(target.ExecutablePath))
            .Select(target => target with
            {
                DisplayName = target.DisplayName.Trim(),
                ExecutablePath = target.ExecutablePath.Trim(),
                TitleIncludes = target.TitleIncludes?.Trim() ?? string.Empty,
                TitleExcludes = target.TitleExcludes?.Trim() ?? string.Empty
            })
            .ToList();
        var launchItems = (source.LaunchItems ?? [])
            .Where(item => item is not null && IsLocalPath(item.ExecutablePath))
            .Select(item => item with
            {
                Id = Guid.NewGuid(),
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? Path.GetFileNameWithoutExtension(item.ExecutablePath)
                    : item.DisplayName.Trim(),
                ExecutablePath = item.ExecutablePath.Trim(),
                Arguments = item.Arguments?.Trim() ?? string.Empty,
                WorkingDirectory = IsLocalPath(item.WorkingDirectory, allowEmpty: true)
                    ? item.WorkingDirectory?.Trim() ?? string.Empty
                    : string.Empty,
                Enabled = false
            })
            .ToList();
        var automation = source.Automation ?? new AutomationSettings();
        var mouseTrigger = Enum.IsDefined(automation.MouseTrigger)
            ? automation.MouseTrigger
            : MouseAutomationTrigger.None;

        return source with
        {
            Id = Guid.NewGuid(),
            Name = $"{(string.IsNullOrWhiteSpace(source.Name) ? "导入场景" : source.Name.Trim())}（导入）",
            Hotkey = new HotkeyGesture(),
            Targets = targets,
            LaunchItems = launchItems,
            Mode = Enum.IsDefined(source.Mode) ? source.Mode : SceneMode.HideWindows,
            PrivacyShellMode = Enum.IsDefined(source.PrivacyShellMode)
                ? source.PrivacyShellMode
                : PrivacyDesktopShellMode.FullExplorer,
            Automation = automation with
            {
                IdleMinutes = Math.Clamp(automation.IdleMinutes, 0, 1440),
                CooldownMilliseconds = Math.Clamp(automation.CooldownMilliseconds, 250, 60_000),
                MouseTrigger = automation.EnableLowLevelMouseHook
                    ? mouseTrigger
                    : MouseAutomationTrigger.None
            }
        };
    }

    private static bool IsLocalPath(string? path, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return allowEmpty;
        var normalized = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.IsPathFullyQualified(normalized) &&
            !normalized.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private sealed record SceneExportPackage
    {
        public int SchemaVersion { get; init; } = ExportSchemaVersion;
        public SceneProfile? Scene { get; init; }
    }
}
