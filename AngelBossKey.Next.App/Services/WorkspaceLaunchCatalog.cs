using AngelBossKey.Next.Core.Models;
using System.IO;
using System.Text.Json;

namespace AngelBossKey.Next.App.Services;

internal static class WorkspaceLaunchCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static (string SceneName, IReadOnlyList<WorkspaceLaunchItem> Items) Load(Guid sceneId)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AngelBossKey.Next",
                "settings.json");
            if (!File.Exists(path)) return ("独立工作区", []);
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            var scene = settings?.Scenes?.FirstOrDefault(item => item.Id == sceneId);
            return scene is null
                ? ("独立工作区", [])
                : (scene.Name, scene.LaunchItems?.Where(item => item.Enabled).ToList() ?? []);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return ("独立工作区", []);
        }
    }

    internal static bool TryLaunch(WorkspaceLaunchItem item, out string error)
    {
        try
        {
            if (!File.Exists(item.ExecutablePath))
            {
                error = $"找不到 {item.DisplayName}。";
                return false;
            }
            var directory = !string.IsNullOrWhiteSpace(item.WorkingDirectory) &&
                Directory.Exists(item.WorkingDirectory)
                ? item.WorkingDirectory
                : Path.GetDirectoryName(item.ExecutablePath) ?? string.Empty;
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.ExecutablePath,
                Arguments = item.Arguments,
                WorkingDirectory = directory,
                UseShellExecute = false
            });
            error = string.Empty;
            return process is not null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
