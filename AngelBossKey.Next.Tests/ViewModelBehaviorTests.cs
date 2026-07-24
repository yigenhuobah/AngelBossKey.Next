using AngelBossKey.Next.App.ViewModels;
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

    private static MainWindowViewModel CreateViewModel(
        ISettingsStore settingsStore,
        FakeVisibilityController controller) =>
        new(new AppSettings(), settingsStore, controller, new FakeStartupRegistration(), new GlobalHotkeyService());

    private sealed class FailingSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Test write failure"));
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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

        public Task<bool> TryHideNewWindowAsync(long handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ForgetDestroyedWindowAsync(long handle, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
