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

        Assert.Equal(2, loaded.SchemaVersion);
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
    public async Task SettingsStore_MigratesVersionOneRulesToVersionTwo()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":1,"targets":[{"executablePath":"C:\\Apps\\editor.exe","displayName":"Editor","enabled":true}]}""");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Equal(2, loaded.SchemaVersion);
        var rule = Assert.Single(loaded.Targets);
        Assert.Equal(string.Empty, rule.TitleIncludes);
        Assert.Equal(string.Empty, rule.TitleExcludes);
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
                    WasForeground = true
                }
            ]
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        await store.ClearAsync();

        Assert.Single(loaded.Windows);
        Assert.Equal(state.Windows[0], loaded.Windows[0]);
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
