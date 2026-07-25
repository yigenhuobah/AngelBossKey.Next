using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using System.Runtime.InteropServices;

namespace AngelBossKey.Next.Win32;

public sealed class PrivacyDesktopService : IPrivacyDesktopService
{
    private const int EmergencyHotkeyId = 0xB7FE;
    private const uint EmergencyModifiers = 0x0001 | 0x0002 | 0x0004 | NativeMethods.ModNoRepeat;
    private const uint F12VirtualKey = 0x7B;
    private readonly IDiagnosticLog _log;
    private readonly nint _originalDesktop;
    private readonly TaskCompletionSource<DesktopContext> _desktopReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _desktopThread;
    private readonly ManualResetEventSlim _threadStarted = new();
    private readonly CancellationTokenSource _shutdown = new();
    private DesktopContext? _context;
    private uint _desktopThreadId;
    private int _activeShellMode = -1;
    private int _workspaceShellMode = -1;
    private bool _isActive;
    private bool _disposed;

    public PrivacyDesktopService(IDiagnosticLog? diagnosticLog = null)
    {
        _log = diagnosticLog ?? NullDiagnosticLog.Instance;
        _originalDesktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        _desktopThread = new Thread(DesktopThreadMain)
        {
            IsBackground = true,
            Name = "AngelBossKey.PrivacyDesktop"
        };
        _desktopThread.SetApartmentState(ApartmentState.STA);
        _desktopThread.Start();
    }

    public bool IsActive => Volatile.Read(ref _isActive);
    public bool HasWorkspace
    {
        get
        {
            var mode = Volatile.Read(ref _workspaceShellMode);
            return mode >= 0 && _context?.HasWorkspace((PrivacyDesktopShellMode)mode) == true;
        }
    }
    public int RunningApplicationCount => GetRunningApplications().Count;
    public PrivacyDesktopShellMode? ActiveShellMode => Volatile.Read(ref _workspaceShellMode) is var mode && mode >= 0
        ? (PrivacyDesktopShellMode)mode
        : null;
    public event EventHandler? StateChanged;

    public async Task<(bool Success, string Message)> EnterAsync(
        PrivacyDesktopLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        var shellMode = request.ShellMode;
        var requestedShellMode = shellMode;
        var shellMessage = string.Empty;
        var workspaceWasCreated = false;
        if (IsActive)
        {
            return (true, "已位于独立隐私桌面。使用 Ctrl+Alt+Shift+F12 紧急返回。");
        }

        var compatibility = CheckCompatibility();
        if (compatibility is not null)
        {
            return (false, compatibility);
        }

        DesktopContext context;
        try
        {
            context = await _desktopReady.Task.WaitAsync(cancellationToken);
            var existingMode = ActiveShellMode;
            if (existingMode is not null && context.HasWorkspace(existingMode.Value))
            {
                shellMode = existingMode.Value;
                var existingShell = await EnsureShellReadyAsync(
                    context,
                    shellMode,
                    request.SceneId,
                    request.ShowToolbar,
                    cancellationToken);
                if (!existingShell.Success) return (false, existingShell.Message);
                shellMessage = "已重新进入保留的独立工作区。";
            }
            else
            {
                var shell = await PrepareShellAsync(
                    context,
                    shellMode,
                    request.SceneId,
                    request.ShowToolbar,
                    cancellationToken);
                if (!shell.Success) return (false, shell.Message);
                shellMode = shell.Mode;
                shellMessage = shell.Message;
                workspaceWasCreated = true;
                Volatile.Write(ref _workspaceShellMode, (int)shellMode);
                var launched = context.LaunchItems(shellMode, request.LaunchItems);
                if (launched.Started > 0 || launched.Failed > 0)
                {
                    shellMessage = $"{shellMessage} 已启动 {launched.Started} 项，失败 {launched.Failed} 项。";
                    _log.Info("desktop.launch-items", $"started={launched.Started}; failed={launched.Failed}");
                }
            }
        }
        catch (Exception exception)
        {
            return (false, $"独立桌面初始化失败：{exception.Message}");
        }

        if (context.Desktop == 0 || !NativeMethods.SwitchDesktop(context.Desktop))
        {
            _log.Warning("desktop.switch", $"failed=true; error={Marshal.GetLastWin32Error()}");
            if (_originalDesktop != 0)
            {
                NativeMethods.SwitchDesktop(_originalDesktop);
            }
            if (workspaceWasCreated)
            {
                context.CloseWorkspace(shellMode);
                Volatile.Write(ref _workspaceShellMode, -1);
            }
            return (false, "无法切换到独立桌面，可能处于安全桌面或当前会话不允许切换。");
        }

        Volatile.Write(ref _isActive, true);
        Volatile.Write(ref _activeShellMode, (int)shellMode);
        if (!context.IsReady(shellMode))
        {
            if (_originalDesktop != 0) NativeMethods.SwitchDesktop(_originalDesktop);
            Volatile.Write(ref _isActive, false);
            Volatile.Write(ref _activeShellMode, -1);
            if (workspaceWasCreated)
            {
                context.CloseWorkspace(shellMode);
                Volatile.Write(ref _workspaceShellMode, -1);
            }
            _log.Warning("desktop.switch", "shell-exited-before-confirmation=true");
            return (false, "隐私桌面 Shell 在切换时已退出，已自动返回原桌面。");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        _log.Info("desktop.enter", "success=true");
        var modeText = shellMode == PrivacyDesktopShellMode.FullExplorer
            ? "完整 Explorer 桌面"
            : "兼容轻量桌面";
        var showShellMessage = requestedShellMode != shellMode ||
            shellMessage.Contains("已启动", StringComparison.Ordinal) ||
            shellMessage.Contains("失败", StringComparison.Ordinal);
        var detailText = showShellMessage ? $"{shellMessage} " : string.Empty;
        return (true, $"已进入{modeText}。{detailText}按 Ctrl+Alt+Shift+F12 紧急返回。");
    }

    public Task<(bool Success, string Message)> ReturnAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActive)
        {
            return Task.FromResult((true, "当前已在原桌面。"));
        }

        if (_originalDesktop == 0 || !NativeMethods.SwitchDesktop(_originalDesktop))
        {
            _log.Warning("desktop.return", $"failed=true; error={Marshal.GetLastWin32Error()}");
            return Task.FromResult((false, "返回原桌面失败，请使用 Ctrl+Alt+Shift+F12 重试。"));
        }

        MarkReturned("desktop.return");
        return Task.FromResult((true, "已返回原桌面。"));
    }

    public Task<(bool Success, string Message)> CloseWorkspaceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsActive && (_originalDesktop == 0 || !NativeMethods.SwitchDesktop(_originalDesktop)))
        {
            _log.Warning("desktop.close", $"return-failed=true; error={Marshal.GetLastWin32Error()}");
            return Task.FromResult((false, "无法返回原桌面，工作区没有关闭。"));
        }

        var mode = ActiveShellMode;
        if (IsActive) MarkReturned("desktop.close-return");
        if (mode is not null) _context?.CloseWorkspace(mode.Value);
        Volatile.Write(ref _workspaceShellMode, -1);
        Volatile.Write(ref _activeShellMode, -1);
        if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty);
        _log.Info("desktop.close", "success=true");
        return Task.FromResult((true, "独立工作区已关闭。"));
    }

    public IReadOnlyList<WorkspaceProcessInfo> GetRunningApplications()
    {
        var mode = ActiveShellMode;
        return mode is null ? [] : _context?.GetRunningApplications(mode.Value) ?? [];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsActive && _originalDesktop != 0)
        {
            NativeMethods.SwitchDesktop(_originalDesktop);
        }
        _shutdown.Cancel();
        var started = _threadStarted.Wait(TimeSpan.FromSeconds(2));
        if (started && _desktopThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_desktopThreadId, NativeMethods.WmQuit, 0, 0);
        }
        var stopped = started && _desktopThread.Join(TimeSpan.FromSeconds(5));
        if (stopped)
        {
            _threadStarted.Dispose();
            _shutdown.Dispose();
        }
        else
        {
            _log.Warning("desktop.dispose", "thread-stop-timeout=true");
        }
        GC.SuppressFinalize(this);
    }

    private void DesktopThreadMain()
    {
        _desktopThreadId = NativeMethods.GetCurrentThreadId();
        var queueProbe = new NativeMethods.Message();
        NativeMethods.PeekMessageW(ref queueProbe, 0, 0, 0, NativeMethods.PmNoRemove);
        _threadStarted.Set();
        if (_shutdown.IsCancellationRequested)
        {
            _desktopReady.TrySetCanceled(_shutdown.Token);
            return;
        }
        var threadOriginalDesktop = NativeMethods.GetThreadDesktop(_desktopThreadId);
        var access = NativeMethods.DesktopReadObjects |
            NativeMethods.DesktopCreateWindow |
            NativeMethods.DesktopWriteObjects |
            NativeMethods.DesktopSwitchDesktop;
        var desktop = NativeMethods.CreateDesktop(
            $"AngelBossKey.Next.{Environment.ProcessId}",
            0,
            0,
            0,
            access,
            0);
        if (desktop == 0 || !NativeMethods.SetThreadDesktop(desktop))
        {
            if (desktop != 0)
            {
                NativeMethods.CloseDesktop(desktop);
            }
            _desktopReady.TrySetException(new InvalidOperationException(
                $"CreateDesktop/SetThreadDesktop failed ({Marshal.GetLastWin32Error()})."));
            return;
        }

        if (!NativeMethods.RegisterHotKey(0, EmergencyHotkeyId, EmergencyModifiers, F12VirtualKey))
        {
            NativeMethods.SetThreadDesktop(threadOriginalDesktop);
            NativeMethods.CloseDesktop(desktop);
            _desktopReady.TrySetException(new InvalidOperationException("紧急返回热键注册失败。"));
            return;
        }

        var compatibleShell = new DesktopShellHost(
            desktop,
            $"AngelBossKey.Next.{Environment.ProcessId}",
            _desktopThreadId,
            _log);
        var explorerShell = new ExplorerDesktopShellHost(
            desktop,
            $"AngelBossKey.Next.{Environment.ProcessId}",
            _desktopThreadId,
            _log);
        compatibleShell.Exited += (_, _) =>
            NativeMethods.PostThreadMessageW(
                _desktopThreadId,
                PrivacyDesktopShellBridge.ShellExitedMessage,
                (nuint)PrivacyDesktopShellMode.Compatibility + 1,
                0);
        explorerShell.Exited += (_, _) =>
            NativeMethods.PostThreadMessageW(
                _desktopThreadId,
                PrivacyDesktopShellBridge.ShellExitedMessage,
                (nuint)PrivacyDesktopShellMode.FullExplorer + 1,
                0);
        try
        {
            _context = new DesktopContext(desktop, compatibleShell, explorerShell);
            _desktopReady.TrySetResult(_context);
            var message = new NativeMethods.Message();
            while (!_shutdown.IsCancellationRequested &&
                NativeMethods.GetMessageW(ref message, 0, 0, 0) > 0)
            {
                if (message.Id == NativeMethods.WmHotkey && (int)message.WParam == EmergencyHotkeyId)
                {
                    if (_originalDesktop != 0 && NativeMethods.SwitchDesktop(_originalDesktop))
                    {
                        MarkReturned("desktop.emergency-return");
                    }
                    continue;
                }

                if (message.Id == PrivacyDesktopShellBridge.ReturnRequestMessage)
                {
                    var shellWindow = (nint)message.WParam;
                    var returned = _originalDesktop != 0 && NativeMethods.SwitchDesktop(_originalDesktop);
                    if (returned)
                    {
                        MarkReturned("desktop.shell-return");
                    }
                    var acknowledged = NativeMethods.PostMessageW(
                        shellWindow,
                        returned
                            ? PrivacyDesktopShellBridge.ReturnSucceededMessage
                            : PrivacyDesktopShellBridge.ReturnFailedMessage,
                        0,
                        0);
                    if (returned && !acknowledged) compatibleShell.Reset();
                    continue;
                }

                if (message.Id == PrivacyDesktopShellBridge.CloseWorkspaceRequestMessage)
                {
                    var mode = ActiveShellMode;
                    if (_originalDesktop != 0 && NativeMethods.SwitchDesktop(_originalDesktop))
                    {
                        MarkReturned("desktop.shell-close-return");
                        if (mode is not null) _context?.CloseWorkspace(mode.Value);
                        Volatile.Write(ref _workspaceShellMode, -1);
                        if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty);
                        _log.Info("desktop.shell-close", "success=true");
                    }
                    continue;
                }

                if (message.Id == PrivacyDesktopShellBridge.ShellExitedMessage)
                {
                    var exitedMode = (int)message.WParam - 1;
                    if (IsActive && exitedMode == Volatile.Read(ref _activeShellMode) &&
                        _originalDesktop != 0 && NativeMethods.SwitchDesktop(_originalDesktop))
                    {
                        MarkReturned("desktop.shell-exited");
                        _log.Warning("desktop.shell-exited", "workspace-preserved=true");
                    }
                    continue;
                }

                NativeMethods.TranslateMessage(in message);
                NativeMethods.DispatchMessageW(in message);
            }
        }
        finally
        {
            if (_originalDesktop != 0) NativeMethods.SwitchDesktop(_originalDesktop);
            MarkReturned("desktop.thread-stop");
            NativeMethods.UnregisterHotKey(0, EmergencyHotkeyId);
            explorerShell.Dispose();
            compatibleShell.Dispose();
            _context = null;
            Volatile.Write(ref _workspaceShellMode, -1);
            NativeMethods.SetThreadDesktop(threadOriginalDesktop);
            NativeMethods.CloseDesktop(desktop);
        }
    }

    private async Task<ShellPreparationResult> PrepareShellAsync(
        DesktopContext context,
        PrivacyDesktopShellMode requestedMode,
        Guid sceneId,
        bool showToolbar,
        CancellationToken cancellationToken)
    {
        if (requestedMode == PrivacyDesktopShellMode.Compatibility)
        {
            context.ExplorerShell.Stop();
            var compatible = await Task.Run(
                () => context.CompatibleShell.EnsureReady(sceneId, cancellationToken),
                cancellationToken);
            return new ShellPreparationResult(
                compatible.Success,
                PrivacyDesktopShellMode.Compatibility,
                compatible.Message);
        }

        context.CompatibleShell.Stop();
        var explorer = await Task.Run(
            () => context.ExplorerShell.EnsureReady(sceneId, showToolbar, cancellationToken),
            cancellationToken);
        if (explorer.Success)
        {
            return new ShellPreparationResult(true, PrivacyDesktopShellMode.FullExplorer, explorer.Message);
        }

        var fallback = await Task.Run(
            () => context.CompatibleShell.EnsureReady(sceneId, cancellationToken),
            cancellationToken);
        var message = fallback.Success
            ? $"{explorer.Message} 已自动回退到兼容轻量桌面。"
            : $"{explorer.Message} 兼容轻量桌面也未能启动：{fallback.Message}";
        _log.Warning("desktop.shell.fallback", $"success={fallback.Success}");
        return new ShellPreparationResult(
            fallback.Success,
            PrivacyDesktopShellMode.Compatibility,
            message);
    }

    private static async Task<(bool Success, string Message)> EnsureShellReadyAsync(
        DesktopContext context,
        PrivacyDesktopShellMode mode,
        Guid sceneId,
        bool showToolbar,
        CancellationToken cancellationToken) => mode == PrivacyDesktopShellMode.FullExplorer
            ? await Task.Run(
                () => context.ExplorerShell.EnsureReady(sceneId, showToolbar, cancellationToken),
                cancellationToken)
            : await Task.Run(
                () => context.CompatibleShell.EnsureReady(sceneId, cancellationToken),
                cancellationToken);

    private sealed record DesktopContext(
        nint Desktop,
        DesktopShellHost CompatibleShell,
        ExplorerDesktopShellHost ExplorerShell)
    {
        internal bool IsReady(PrivacyDesktopShellMode mode) =>
            mode == PrivacyDesktopShellMode.FullExplorer
                ? ExplorerShell.IsReady
                : CompatibleShell.IsReady;

        internal bool HasWorkspace(PrivacyDesktopShellMode mode) =>
            mode == PrivacyDesktopShellMode.FullExplorer
                ? ExplorerShell.HasWorkspace
                : CompatibleShell.HasWorkspace;

        internal (int Started, int Failed) LaunchItems(
            PrivacyDesktopShellMode mode,
            IEnumerable<WorkspaceLaunchItem> launchItems) =>
            mode == PrivacyDesktopShellMode.FullExplorer
                ? ExplorerShell.LaunchItems(launchItems)
                : CompatibleShell.LaunchItems(launchItems);

        internal IReadOnlyList<WorkspaceProcessInfo> GetRunningApplications(
            PrivacyDesktopShellMode mode) =>
            mode == PrivacyDesktopShellMode.FullExplorer
                ? ExplorerShell.GetRunningApplications()
                : CompatibleShell.GetRunningApplications();

        internal void CloseWorkspace(PrivacyDesktopShellMode mode)
        {
            if (mode == PrivacyDesktopShellMode.FullExplorer) ExplorerShell.Reset();
            else CompatibleShell.Reset();
        }
    }

    private sealed record ShellPreparationResult(
        bool Success,
        PrivacyDesktopShellMode Mode,
        string Message);

    private void MarkReturned(string eventName)
    {
        var wasActive = Volatile.Read(ref _isActive);
        Volatile.Write(ref _isActive, false);
        Volatile.Write(ref _activeShellMode, -1);
        if (!wasActive) return;
        if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty);
        _log.Info(eventName, "success=true");
    }

    private static string? CheckCompatibility()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0 || !NativeMethods.GetWindowRect(foreground, out var windowRect))
        {
            return null;
        }

        var monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (monitor == 0 || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var isFullscreen = windowRect.Left <= info.Monitor.Left &&
            windowRect.Top <= info.Monitor.Top &&
            windowRect.Right >= info.Monitor.Right &&
            windowRect.Bottom >= info.Monitor.Bottom;
        return isFullscreen
            ? "检测到独占或全屏前台窗口。为避免游戏/视频切换异常，本次未进入独立桌面。"
            : null;
    }
}
