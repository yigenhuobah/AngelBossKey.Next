using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Core.Abstractions;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IRecoveryStore
{
    Task<RecoveryState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RecoveryState state, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IWindowCatalog
{
    IReadOnlyList<WindowInfo> GetVisibleWindows();
    WindowInfo? TryGetWindow(long handle);
}

public interface IWindowVisibilityController
{
    bool IsHidden { get; }
    event EventHandler? StateChanged;

    Task<VisibilityOperationResult> HideAsync(
        IReadOnlyCollection<TargetRule> targets,
        CancellationToken cancellationToken = default);

    Task<VisibilityOperationResult> RestoreAsync(CancellationToken cancellationToken = default);
    Task<VisibilityOperationResult> RecoverAsync(CancellationToken cancellationToken = default);
    Task<VisibilityOperationResult> UpdateTargetsAsync(
        IReadOnlyCollection<TargetRule> targets,
        CancellationToken cancellationToken = default);
    Task<VisibilityOperationResult> SelfCheckAsync(CancellationToken cancellationToken = default);
    Task<bool> TryHideNewWindowAsync(long handle, CancellationToken cancellationToken = default);
    Task ForgetDestroyedWindowAsync(long handle, CancellationToken cancellationToken = default);
}

public interface IDiagnosticLog
{
    void Info(string eventName, string details);
    void Warning(string eventName, string details);
    void Error(string eventName, Exception exception);
}

public interface IStartupRegistration
{
    bool IsEnabledFor(string executablePath);
    void SetEnabled(bool enabled, string executablePath);
}
