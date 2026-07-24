using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using AngelBossKey.Next.Core.Storage;
using AngelBossKey.Next.Win32;

namespace AngelBossKey.Next.Tests;

public sealed class CoreBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AngelBossKey.Next.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RuleMatcher_MatchesNormalizedPathIgnoringCase()
    {
        var window = CreateWindowInfo(@"C:\Apps\Editor\editor.exe");
        var targets = new[]
        {
            new TargetRule
            {
                DisplayName = "Editor",
                ExecutablePath = @"c:\apps\editor\.\EDITOR.exe"
            }
        };

        Assert.True(TargetRuleMatcher.Matches(window, targets));
    }

    [Fact]
    public void RuleMatcher_DoesNotMatchDisabledRule()
    {
        var window = CreateWindowInfo(@"C:\Apps\Editor\editor.exe");
        var targets = new[]
        {
            new TargetRule
            {
                DisplayName = "Editor",
                ExecutablePath = window.ExecutablePath,
                Enabled = false
            }
        };

        Assert.False(TargetRuleMatcher.Matches(window, targets));
    }

    [Fact]
    public void RuleMatcher_AppliesOptionalTitleIncludeAndExcludeConditions()
    {
        var window = CreateWindowInfo(@"C:\Apps\Editor\editor.exe") with
        {
            Title = "Quarterly report - Private"
        };
        var includeRule = new TargetRule
        {
            DisplayName = "Editor",
            ExecutablePath = window.ExecutablePath,
            TitleIncludes = "quarterly"
        };
        var excludeRule = includeRule with { TitleExcludes = "private" };

        Assert.True(TargetRuleMatcher.Matches(window, [includeRule]));
        Assert.False(TargetRuleMatcher.Matches(window, [excludeRule]));
    }

    [Fact]
    public async Task SettingsStore_RoundTripsHotkeyTargetsAndPreferences()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new AppSettings
        {
            Hotkey = new HotkeyGesture
            {
                Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                VirtualKey = 0x20
            },
            LaunchAtLogin = true,
            CloseToTray = false,
            Targets =
            [
                new TargetRule
                {
                    DisplayName = "Editor",
                    ExecutablePath = @"C:\Apps\editor.exe",
                    TitleIncludes = "Document",
                    TitleExcludes = "Private"
                }
            ]
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(settings.Hotkey, loaded.Hotkey);
        Assert.True(loaded.LaunchAtLogin);
        Assert.False(loaded.CloseToTray);
        Assert.Single(loaded.Targets);
        Assert.Equal(settings.Targets[0], loaded.Targets[0]);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SettingsStore_ReturnsDefaultsForCorruptJson()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{broken");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Equal(6, loaded.SchemaVersion);
        Assert.False(loaded.Hotkey.IsConfigured);
        Assert.Empty(loaded.Targets);
    }

    [Fact]
    public async Task SettingsStore_NormalizesNullCollectionsFromExternalEdits()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, """{"schemaVersion":1,"hotkey":null,"targets":null}""");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.NotNull(loaded.Hotkey);
        Assert.Empty(loaded.Targets);
    }

    [Fact]
    public async Task SettingsStore_NormalizesNullLegacyHotkeyInCurrentSchema()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":6,"hotkey":null,"targets":[],"scenes":[]}""");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Single(loaded.Scenes);
        Assert.False(loaded.Hotkey.IsConfigured);
    }

    [Fact]
    public async Task SettingsStore_MigratesVersionOneRulesToDefaultScene()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":1,"targets":[{"executablePath":"C:\\Apps\\editor.exe","displayName":"Editor","enabled":true}]}""");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Equal(6, loaded.SchemaVersion);
        var scene = Assert.Single(loaded.Scenes);
        Assert.Equal("默认场景", scene.Name);
        var rule = Assert.Single(loaded.Targets);
        Assert.Equal(string.Empty, rule.TitleIncludes);
        Assert.Equal(string.Empty, rule.TitleExcludes);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsMultipleScenesAndAutomation()
    {
        var path = Path.Combine(_directory, "settings.json");
        var first = new SceneProfile
        {
            Name = "工作",
            Hotkey = new HotkeyGesture
            {
                Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                VirtualKey = 0x31
            },
            Targets =
            [
                new TargetRule
                {
                    DisplayName = "Editor",
                    ExecutablePath = @"C:\Apps\editor.exe",
                    MuteWhenHidden = true
                }
            ],
            Automation = new AutomationSettings
            {
                IdleMinutes = 5,
                MouseTrigger = MouseAutomationTrigger.XButton1,
                EnableLowLevelMouseHook = true,
                CooldownMilliseconds = 750
            }
        };
        var second = new SceneProfile
        {
            Name = "桌面",
            Mode = SceneMode.PrivacyDesktop,
            Hotkey = new HotkeyGesture
            {
                Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
                VirtualKey = 0x32
            }
        };
        var store = new JsonSettingsStore(path);

        await store.SaveAsync(new AppSettings
        {
            Scenes = [first, second],
            ActiveSceneId = second.Id,
            EnableElevatedBroker = true
        });
        var loaded = await store.LoadAsync();

        Assert.Equal(6, loaded.SchemaVersion);
        Assert.Equal(second.Id, loaded.ActiveSceneId);
        Assert.True(loaded.EnableElevatedBroker);
        Assert.Equal(2, loaded.Scenes.Count);
        Assert.True(loaded.Scenes[0].Targets[0].MuteWhenHidden);
        Assert.Equal(MouseAutomationTrigger.XButton1, loaded.Scenes[0].Automation.MouseTrigger);
        Assert.Equal(SceneMode.PrivacyDesktop, loaded.Scenes[1].Mode);
    }

    [Fact]
    public async Task SettingsStore_DisablesMouseTriggerUnlessHookIsExplicitlyEnabled()
    {
        var path = Path.Combine(_directory, "settings.json");
        var scene = new SceneProfile
        {
            Automation = new AutomationSettings
            {
                MouseTrigger = MouseAutomationTrigger.WheelDown,
                EnableLowLevelMouseHook = false
            }
        };
        await new JsonSettingsStore(path).SaveAsync(new AppSettings
        {
            Scenes = [scene],
            ActiveSceneId = scene.Id
        });

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Equal(MouseAutomationTrigger.None, Assert.Single(loaded.Scenes).Automation.MouseTrigger);
    }

    [Fact]
    public async Task RecoveryStore_RoundTripsAndClearsJournal()
    {
        var path = Path.Combine(_directory, "recovery.json");
        var store = new JsonRecoveryStore(path);
        var state = new RecoveryState
        {
            Windows =
            [
                new HiddenWindowRecord
                {
                    Handle = 1234,
                    ProcessId = 42,
                    ProcessStartTimeUtcTicks = 100,
                    ExecutablePath = @"C:\Apps\editor.exe",
                    Placement = new WindowPlacementSnapshot
                    {
                        ShowCommand = 3,
                        Left = 10,
                        Top = 20,
                        Right = 800,
                        Bottom = 600
                    },
                    WasForeground = true,
                    RequiresElevatedBroker = true
                }
            ]
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        await store.ClearAsync();

        Assert.Single(loaded.Windows);
        Assert.Equal(state.Windows[0], loaded.Windows[0]);
        Assert.True(loaded.Windows[0].RequiresElevatedBroker);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RecoveryStore_DropsRecordsWithoutAStableProcessIdentity()
    {
        var path = Path.Combine(_directory, "recovery.json");
        var store = new JsonRecoveryStore(path);
        await store.SaveAsync(new RecoveryState
        {
            Windows =
            [
                new HiddenWindowRecord
                {
                    Handle = 1234,
                    ProcessId = 42,
                    ProcessStartTimeUtcTicks = 0,
                    ExecutablePath = @"C:\Apps\editor.exe",
                    Placement = new WindowPlacementSnapshot { ShowCommand = 1 }
                }
            ]
        });

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Windows);
    }

    [Fact]
    public async Task AudioRecoveryStore_RoundTripsOriginalSessionStateAndClears()
    {
        var path = Path.Combine(_directory, "audio-recovery.json");
        var store = new JsonAudioRecoveryStore(path);
        var state = new AudioRecoveryState
        {
            Sessions =
            [
                new AudioSessionSnapshot
                {
                    SessionId = "device|session",
                    ProcessId = 42,
                    ProcessStartTimeUtcTicks = 100,
                    ExecutablePath = @"C:\Apps\player.exe",
                    Volume = 0.42f,
                    Muted = false
                }
            ]
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        await store.ClearAsync();

        Assert.Equal(state.Sessions[0], Assert.Single(loaded.Sessions));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void StartupCommand_QuotesExecutablePathAndUsesBackgroundMode()
    {
        var command = StartupRegistration.BuildCommand(@"F:\Program Files\Angel BossKey\AngelBossKey.Next.exe");

        Assert.Equal(
            "\"F:\\Program Files\\Angel BossKey\\AngelBossKey.Next.exe\" --background",
            command);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static WindowInfo CreateWindowInfo(string path) => new()
    {
        Handle = 1,
        ProcessId = 2,
        Title = "Document",
        ProcessName = "editor",
        DisplayName = "Editor",
        ExecutablePath = path
    };
}
