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

public interface IAudioRecoveryStore
{
    Task<AudioRecoveryState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AudioRecoveryState state, CancellationToken cancellationToken = default);
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

public interface IApplicationAudioController : IDisposable
{
    bool IsActive { get; }
    int PendingRestoreCount { get; }
    Task<int> MuteAsync(IReadOnlyCollection<TargetRule> targets, CancellationToken cancellationToken = default);
    Task<int> RestoreAsync(CancellationToken cancellationToken = default);
    Task<int> RecoverAsync(CancellationToken cancellationToken = default);
    Task ReconcileAsync(IReadOnlyCollection<TargetRule> targets, CancellationToken cancellationToken = default);
}

public interface IAutomationTriggerService : IDisposable
{
    event EventHandler<AutomationTriggeredEventArgs>? Triggered;
    bool IsPaused { get; set; }
    void Configure(AutomationSettings settings);
}

public interface IElevatedWindowBroker
{
    bool IsEnabled { get; set; }
    Task<ElevatedWindowResponse> ExecuteAsync(
        ElevatedWindowRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPrivacyDesktopService : IDisposable
{
    bool IsActive { get; }
    event EventHandler? StateChanged;
    Task<(bool Success, string Message)> EnterAsync(CancellationToken cancellationToken = default);
    Task<(bool Success, string Message)> ReturnAsync(CancellationToken cancellationToken = default);
}
