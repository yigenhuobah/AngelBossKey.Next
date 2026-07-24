using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using System.Text.Json;

namespace AngelBossKey.Next.Core.Storage;

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await AtomicJsonStore.ReadAsync<AppSettings>(path, cancellationToken);
            if (settings is null || settings.SchemaVersion is < 1 or > 6)
            {
                return CreateDefaults();
            }

            var legacyTargets = NormalizeTargets(settings.Targets);
            var scenes = (settings.Scenes ?? [])
                .Where(scene => scene is not null)
                .Select(scene => scene with
                {
                    Name = string.IsNullOrWhiteSpace(scene.Name) ? "未命名场景" : scene.Name.Trim(),
                    Hotkey = scene.Hotkey ?? new HotkeyGesture(),
                    Targets = NormalizeTargets(scene.Targets),
                    Automation = NormalizeAutomation(scene.Automation)
                })
                .ToList();

            var usedSceneIds = new HashSet<Guid>();
            scenes = scenes.Select(scene =>
            {
                var id = scene.Id;
                if (id == Guid.Empty || !usedSceneIds.Add(id))
                {
                    id = Guid.NewGuid();
                    usedSceneIds.Add(id);
                }
                return scene with { Id = id };
            }).ToList();

            if (scenes.Count == 0 && (settings.SchemaVersion <= 2 ||
                settings.Hotkey?.IsConfigured == true || legacyTargets.Count > 0))
            {
                scenes.Add(new SceneProfile
                {
                    Name = "默认场景",
                    Hotkey = settings.Hotkey ?? new HotkeyGesture(),
                    Targets = legacyTargets
                });
            }

            if (scenes.Count == 0)
            {
                scenes.Add(new SceneProfile());
            }

            var activeSceneId = scenes.Any(scene => scene.Id == settings.ActiveSceneId)
                ? settings.ActiveSceneId
                : scenes[0].Id;

            return settings with
            {
                SchemaVersion = 6,
                Hotkey = scenes.First(scene => scene.Id == activeSceneId).Hotkey,
                Targets = [.. scenes.First(scene => scene.Id == activeSceneId).Targets],
                Scenes = scenes,
                ActiveSceneId = activeSceneId
            };
        }
        catch (JsonException)
        {
            return CreateDefaults();
        }
        catch (IOException)
        {
            return CreateDefaults();
        }
        catch (UnauthorizedAccessException)
        {
            return CreateDefaults();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await AtomicJsonStore.WriteAsync(path, settings, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static List<TargetRule> NormalizeTargets(IEnumerable<TargetRule>? targets) =>
        (targets ?? [])
            .Where(target => target is not null &&
                !string.IsNullOrWhiteSpace(target.DisplayName) &&
                !string.IsNullOrWhiteSpace(target.ExecutablePath))
            .Select(target => target with
            {
                TitleIncludes = target.TitleIncludes?.Trim() ?? string.Empty,
                TitleExcludes = target.TitleExcludes?.Trim() ?? string.Empty
            })
            .ToList();

    private static AutomationSettings NormalizeAutomation(AutomationSettings? automation)
    {
        automation ??= new AutomationSettings();
        return automation with
        {
            IdleMinutes = Math.Clamp(automation.IdleMinutes, 0, 1440),
            CooldownMilliseconds = Math.Clamp(automation.CooldownMilliseconds, 250, 60_000),
            MouseTrigger = automation.EnableLowLevelMouseHook
                ? automation.MouseTrigger
                : MouseAutomationTrigger.None
        };
    }

    private static AppSettings CreateDefaults()
    {
        var scene = new SceneProfile();
        return new AppSettings
        {
            Scenes = [scene],
            ActiveSceneId = scene.Id
        };
    }
}
