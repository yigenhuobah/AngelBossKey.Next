using AngelBossKey.Next.App.ViewModels;
using AngelBossKey.Next.App.Services;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using AngelBossKey.Next.Win32;

namespace AngelBossKey.Next.Tests;

public sealed class ViewModelBehaviorTests
{
    [Fact]
    public async Task AddTargets_RollsBackAndReportsWhenSettingsCannotBeSaved()
    {
        var controller = new FakeVisibilityController();
        var viewModel = CreateViewModel(new FailingSettingsStore(), controller);

        await viewModel.AddTargetsAsync(
        [
            new WindowInfo
            {
                Handle = 1,
                ProcessId = 2,
                Title = "Document",
                ProcessName = "editor",
                DisplayName = "Editor",
                ExecutablePath = @"C:\Apps\editor.exe"
            }
        ]);

        Assert.Empty(viewModel.Targets);
        Assert.StartsWith("添加目标失败", viewModel.Message);
    }

    [Fact]
    public async Task RestoreRemainsAvailableWithoutHotkeyOrEnabledTargets()
    {
        var controller = new FakeVisibilityController { IsHidden = true };
        var viewModel = CreateViewModel(new MemorySettingsStore(), controller);

        Assert.True(viewModel.CanToggle);
        var result = await viewModel.ToggleVisibilityAsync();

        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, controller.RestoreCalls);
    }

    [Fact]
    public void DisablingARuleWhileHidden_ReconcilesOnlyEffectiveRules()
    {
        var controller = new FakeVisibilityController { IsHidden = true };
        var store = new MemorySettingsStore();
        var settings = CreateSettingsWithTargets("Editor");
        var viewModel = CreateViewModel(store, controller, settings);

        viewModel.Targets[0].Enabled = false;

        Assert.True(SpinWait.SpinUntil(() => controller.UpdateCalls > 0, TimeSpan.FromSeconds(3)));
        Assert.False(Assert.Single(controller.LastTargets!).Enabled);
        Assert.False(Assert.Single(store.LastSaved!.Targets).Enabled);
    }

    [Fact]
    public void TemporaryExclusion_IsAppliedButNotPersisted()
    {
        var controller = new FakeVisibilityController { IsHidden = true };
        var store = new MemorySettingsStore();
        var viewModel = CreateViewModel(store, controller, CreateSettingsWithTargets("Editor"));

        viewModel.Targets[0].TemporarilyExcluded = true;

        Assert.True(SpinWait.SpinUntil(() => controller.UpdateCalls > 0, TimeSpan.FromSeconds(3)));
        Assert.False(Assert.Single(controller.LastTargets!).Enabled);
        Assert.True(Assert.Single(store.LastSaved!.Targets).Enabled);
    }

    [Fact]
    public void MoveCommand_PersistsRuleOrder()
    {
        var controller = new FakeVisibilityController();
        var store = new MemorySettingsStore();
        var settings = CreateSettingsWithTargets("First", "Second");
        var viewModel = CreateViewModel(store, controller, settings);

        viewModel.MoveTargetDownCommand.Execute(viewModel.Targets[0]);

        Assert.True(SpinWait.SpinUntil(() => store.LastSaved is not null, TimeSpan.FromSeconds(3)));
        Assert.Equal(["Second", "First"], store.LastSaved!.Targets.Select(target => target.DisplayName));
    }

    [Fact]
    public void MissingExecutable_IsClearlyInvalidAndNotEffective()
    {
        var row = new TargetRowViewModel(new TargetRule
        {
            DisplayName = "Missing",
            ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")
        });

        Assert.False(row.IsPathValid);
        Assert.False(row.EffectiveEnabled);
        Assert.Equal("路径失效", row.StatusText);
    }

    [Fact]
    public void DiagnosticLog_RotatesAtTheConfiguredLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AngelBossKey.Next.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new RollingDiagnosticLog(directory);
            var payload = new string('x', 100_000);
            for (var index = 0; index < 12; index++)
            {
                log.Info("test.event", payload);
            }

            Assert.True(File.Exists(Path.Combine(directory, "angelbosskey.log")));
            Assert.True(File.Exists(Path.Combine(directory, "angelbosskey.log.1")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task WindowStateMonitor_ContinuesAfterATransientFailure()
    {
        var controller = new FakeVisibilityController { FailFirstSelfCheck = true };
        using var monitor = new WindowStateMonitor(
            controller,
            NullDiagnosticLog.Instance,
            TimeSpan.FromMilliseconds(10));

        monitor.Start();
        await controller.SecondSelfCheckReached.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(controller.SelfCheckCalls >= 2);
    }

    [Fact]
    public async Task FlushSettings_WaitsForQueuedRuleChanges()
    {
        var controller = new FakeVisibilityController();
        var store = new BlockingSettingsStore();
        var viewModel = CreateViewModel(store, controller, CreateSettingsWithTargets("First", "Second"));

        viewModel.Targets[0].Enabled = false;
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.Targets[1].Enabled = false;
        var flush = viewModel.FlushSettingsAsync();
        store.ReleaseFirstSave.TrySetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.All(store.LastSaved!.Targets, target => Assert.False(target.Enabled));
    }

    [Fact]
    public void RefreshPathValidity_RaisesTheCurrentPathState()
    {
        var path = Path.GetTempFileName();
        try
        {
            var row = new TargetRowViewModel(new TargetRule
            {
                DisplayName = "Temporary",
                ExecutablePath = path
            });
            Assert.True(row.IsPathValid);

            File.Delete(path);

            Assert.True(row.RefreshPathValidity());
            Assert.False(row.IsPathValid);
            Assert.False(row.EffectiveEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MuteWhenHidden_IsPersistedWithTheActiveScene()
    {
        var store = new MemorySettingsStore();
        var viewModel = CreateViewModel(store, new FakeVisibilityController(), CreateSettingsWithTargets("Editor"));

        viewModel.Targets[0].MuteWhenHidden = true;

        Assert.True(SpinWait.SpinUntil(
            () => store.LastSaved?.Scenes.Single().Targets.Single().MuteWhenHidden == true,
            TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task SwitchingSceneWhileHidden_RestoresBeforeChangingSelection()
    {
        var first = new SceneProfile { Name = "First" };
        var second = new SceneProfile { Name = "Second" };
        var settings = new AppSettings
        {
            Scenes = [first, second],
            ActiveSceneId = first.Id
        };
        var controller = new FakeVisibilityController { IsHidden = true };
        var viewModel = CreateViewModel(new MemorySettingsStore(), controller, settings);

        await viewModel.SelectSceneAsync(viewModel.Scenes[1]);

        Assert.Equal(1, controller.RestoreCalls);
        Assert.Equal("Second", viewModel.SelectedScene.Name);
    }

    [Fact]
    public async Task RejectedSceneSwitch_NotifiesUiToRestoreActualSelection()
    {
        var first = new SceneProfile { Name = "First" };
        var second = new SceneProfile { Name = "Second" };
        var controller = new FakeVisibilityController
        {
            IsHidden = true,
            RestoreLeavesHidden = true
        };
        var viewModel = CreateViewModel(
            new MemorySettingsStore(),
            controller,
            new AppSettings { Scenes = [first, second], ActiveSceneId = first.Id });
        var selectionNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.SelectedScene)) selectionNotifications++;
        };

        var switched = await viewModel.SelectSceneAsync(viewModel.Scenes[1]);

        Assert.False(switched);
        Assert.Equal("First", viewModel.SelectedScene.Name);
        Assert.True(selectionNotifications > 0);
    }

    [Fact]
    public async Task ConcurrentSceneSwitch_FirstFailureCannotOverwriteNewerSuccess()
    {
        var first = new SceneProfile { Name = "First" };
        var second = new SceneProfile { Name = "Second" };
        var third = new SceneProfile { Name = "Third" };
        var controller = new FakeVisibilityController
        {
            IsHidden = true,
            FailBlockedFirstRestore = true
        };
        var viewModel = CreateViewModel(
            new MemorySettingsStore(),
            controller,
            new AppSettings { Scenes = [first, second, third], ActiveSceneId = first.Id });

        var olderRequest = viewModel.SelectSceneAsync(viewModel.Scenes[1]);
        await controller.FirstRestoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newerRequest = viewModel.SelectSceneAsync(viewModel.Scenes[2]);
        controller.ReleaseFirstRestore.TrySetResult();

        Assert.False(await olderRequest);
        Assert.True(await newerRequest);
        Assert.Equal("Third", viewModel.SelectedScene.Name);
    }

    [Fact]
    public async Task IdleAutomation_DoesNotRestoreAnAlreadyHiddenScene()
    {
        var controller = new FakeVisibilityController { IsHidden = true };
        var viewModel = CreateViewModel(new MemorySettingsStore(), controller);

        await viewModel.HandleAutomationAsync(AutomationTriggerSource.Idle);

        Assert.Equal(0, controller.RestoreCalls);
    }

    [Fact]
    public async Task HidingScene_MutesOnlyAfterWindowTransactionStarts()
    {
        var audio = new FakeAudioController();
        var controller = new FakeVisibilityController();
        var settings = CreateSettingsWithTargets("Editor") with
        {
            Hotkey = new HotkeyGesture
            {
                Modifiers = HotkeyModifiers.Control,
                VirtualKey = 0x31
            }
        };
        var viewModel = new MainWindowViewModel(
            settings,
            new MemorySettingsStore(),
            controller,
            new FakeStartupRegistration(),
            new GlobalHotkeyService(),
            audioController: audio);

        await viewModel.ToggleVisibilityAsync();

        Assert.Equal(1, controller.HideCalls);
        Assert.Equal(1, audio.MuteCalls);
    }

    [Fact]
    public async Task SceneSwitch_RollsBackWhenSettingsWriteFails()
    {
        var first = new SceneProfile { Name = "First" };
        var second = new SceneProfile { Name = "Second" };
        var viewModel = CreateViewModel(
            new FailingSettingsStore(),
            new FakeVisibilityController(),
            new AppSettings { Scenes = [first, second], ActiveSceneId = first.Id });

        var switched = await viewModel.SelectSceneAsync(viewModel.Scenes[1]);

        Assert.False(switched);
        Assert.Equal("First", viewModel.SelectedScene.Name);
        Assert.StartsWith("切换场景失败", viewModel.Message);
    }

    [Fact]
    public async Task SceneSwitch_RollsBackWhenAutomationReconfigurationThrows()
    {
        var first = new SceneProfile { Name = "First" };
        var second = new SceneProfile { Name = "Second" };
        var viewModel = new MainWindowViewModel(
            new AppSettings { Scenes = [first, second], ActiveSceneId = first.Id },
            new MemorySettingsStore(),
            new FakeVisibilityController(),
            new FakeStartupRegistration(),
            new GlobalHotkeyService(),
            automationService: new ThrowingAutomationService());

        var switched = await viewModel.SelectSceneAsync(viewModel.Scenes[1]);

        Assert.False(switched);
        Assert.Equal("First", viewModel.SelectedScene.Name);
        Assert.StartsWith("切换场景失败", viewModel.Message);
    }

    [Fact]
    public async Task PrivacyDesktopFailure_FallsBackToWindowHiding()
    {
        var scene = new SceneProfile
        {
            Name = "Privacy",
            Mode = SceneMode.PrivacyDesktop,
            Hotkey = new HotkeyGesture { Modifiers = HotkeyModifiers.Control, VirtualKey = 0x31 },
            Targets =
            [
                new TargetRule { DisplayName = "Editor", ExecutablePath = Environment.ProcessPath! }
            ]
        };
        var controller = new FakeVisibilityController();
        var viewModel = new MainWindowViewModel(
            new AppSettings { Scenes = [scene], ActiveSceneId = scene.Id },
            new MemorySettingsStore(),
            controller,
            new FakeStartupRegistration(),
            new GlobalHotkeyService(),
            privacyDesktop: new FailingPrivacyDesktopService());

        var result = await viewModel.ToggleVisibilityAsync();

        Assert.Equal(1, controller.HideCalls);
        Assert.True(controller.IsHidden);
        Assert.Contains("回退为普通窗口隐藏", result.Detail);
    }

    private static MainWindowViewModel CreateViewModel(
        ISettingsStore settingsStore,
        FakeVisibilityController controller,
        AppSettings? settings = null) =>
        new(settings ?? new AppSettings(), settingsStore, controller, new FakeStartupRegistration(), new GlobalHotkeyService());

    private static AppSettings CreateSettingsWithTargets(params string[] names) => new()
    {
        Targets = names.Select(name =>
            new TargetRule
            {
                DisplayName = name,
                ExecutablePath = Environment.ProcessPath!
            }).ToList()
    };

    private sealed class FailingSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Test write failure"));
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public AppSettings? LastSaved { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            LastSaved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSettingsStore : ISettingsStore
    {
        private int _saveCalls;
        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AppSettings? LastSaved { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCalls) == 1)
            {
                FirstSaveStarted.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }

            LastSaved = settings;
        }
    }

    private sealed class FakeStartupRegistration : IStartupRegistration
    {
        public bool IsEnabledFor(string executablePath) => false;
        public void SetEnabled(bool enabled, string executablePath)
        {
        }
    }

    private sealed class FakeVisibilityController : IWindowVisibilityController
    {
        public bool IsHidden { get; set; }
        public int RestoreCalls { get; private set; }
        public int HideCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public IReadOnlyCollection<TargetRule>? LastTargets { get; private set; }
        public bool FailFirstSelfCheck { get; init; }
        public bool RestoreLeavesHidden { get; init; }
        public bool FailBlockedFirstRestore { get; init; }
        public int SelfCheckCalls { get; private set; }
        public TaskCompletionSource FirstRestoreStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRestore { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSelfCheckReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? StateChanged;

        public Task<VisibilityOperationResult> HideAsync(
            IReadOnlyCollection<TargetRule> targets,
            CancellationToken cancellationToken = default)
        {
            HideCalls++;
            IsHidden = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new VisibilityOperationResult());
        }

        public async Task<VisibilityOperationResult> RestoreAsync(CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            var shouldFail = RestoreLeavesHidden || (FailBlockedFirstRestore && RestoreCalls == 1);
            if (FailBlockedFirstRestore && RestoreCalls == 1)
            {
                FirstRestoreStarted.TrySetResult();
                await ReleaseFirstRestore.Task.WaitAsync(cancellationToken);
            }
            IsHidden = shouldFail;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new VisibilityOperationResult
            {
                ChangedCount = shouldFail ? 0 : 1,
                FailedCount = shouldFail ? 1 : 0
            };
        }

        public Task<VisibilityOperationResult> RecoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<VisibilityOperationResult> UpdateTargetsAsync(
            IReadOnlyCollection<TargetRule> targets,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            LastTargets = targets;
            return Task.FromResult(new VisibilityOperationResult());
        }

        public Task<VisibilityOperationResult> SelfCheckAsync(CancellationToken cancellationToken = default)
        {
            SelfCheckCalls++;
            if (FailFirstSelfCheck && SelfCheckCalls == 1)
            {
                throw new IOException("Transient self-check failure");
            }
            if (SelfCheckCalls >= 2)
            {
                SecondSelfCheckReached.TrySetResult();
            }

            return Task.FromResult(new VisibilityOperationResult());
        }

        public Task<bool> TryHideNewWindowAsync(long handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ForgetDestroyedWindowAsync(long handle, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAudioController : IApplicationAudioController
    {
        public bool IsActive => MuteCalls > RestoreCalls;
        public int PendingRestoreCount => 0;
        public int MuteCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public Task<int> MuteAsync(IReadOnlyCollection<TargetRule> targets, CancellationToken cancellationToken = default)
        {
            MuteCalls++;
            return Task.FromResult(0);
        }
        public Task<int> RestoreAsync(CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            return Task.FromResult(0);
        }
        public Task<int> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ReconcileAsync(IReadOnlyCollection<TargetRule> targets, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FailingPrivacyDesktopService : IPrivacyDesktopService
    {
        public bool IsActive => false;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<(bool Success, string Message)> EnterAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((false, "桌面切换失败。"));
        public Task<(bool Success, string Message)> ReturnAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "已返回。"));
        public void Dispose() { }
    }

    private sealed class ThrowingAutomationService : IAutomationTriggerService
    {
        public event EventHandler<AutomationTriggeredEventArgs>? Triggered { add { } remove { } }
        public bool IsPaused { get; set; }
        public void Configure(AutomationSettings settings) => throw new InvalidOperationException("Test automation failure");
        public void Dispose() { }
    }
}
