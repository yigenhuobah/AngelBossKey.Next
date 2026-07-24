using AngelBossKey.Next.App.ViewModels;
using AngelBossKey.Next.App.Services;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
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
        public int UpdateCalls { get; private set; }
        public IReadOnlyCollection<TargetRule>? LastTargets { get; private set; }
        public event EventHandler? StateChanged;

        public Task<VisibilityOperationResult> HideAsync(
            IReadOnlyCollection<TargetRule> targets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<VisibilityOperationResult> RestoreAsync(CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            IsHidden = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new VisibilityOperationResult { ChangedCount = 1 });
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

        public Task<VisibilityOperationResult> SelfCheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<bool> TryHideNewWindowAsync(long handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ForgetDestroyedWindowAsync(long handle, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
