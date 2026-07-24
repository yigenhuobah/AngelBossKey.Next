using AngelBossKey.Next.Core.Abstractions;
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
    private uint _desktopThreadId;
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
    public event EventHandler? StateChanged;

    public async Task<(bool Success, string Message)> EnterAsync(
        CancellationToken cancellationToken = default)
    {
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
            var shell = await Task.Run(
                () => context.Shell.EnsureReady(cancellationToken),
                cancellationToken);
            if (!shell.Success) return shell;
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
            return (false, "无法切换到独立桌面，可能处于安全桌面或当前会话不允许切换。");
        }

        Volatile.Write(ref _isActive, true);
        if (!context.Shell.IsReady)
        {
            if (_originalDesktop != 0) NativeMethods.SwitchDesktop(_originalDesktop);
            Volatile.Write(ref _isActive, false);
            _log.Warning("desktop.switch", "shell-exited-before-confirmation=true");
            return (false, "隐私桌面 Shell 在切换时已退出，已自动返回原桌面。");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        _log.Info("desktop.enter", "success=true");
        return (true, "已进入独立隐私桌面。按 Ctrl+Alt+Shift+F12 紧急返回。");
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

        var shell = new DesktopShellHost(
            desktop,
            $"AngelBossKey.Next.{Environment.ProcessId}",
            _desktopThreadId,
            _log);
        shell.Exited += (_, _) =>
            NativeMethods.PostThreadMessageW(
                _desktopThreadId,
                PrivacyDesktopShellBridge.ShellExitedMessage,
                0,
                0);
        try
        {
            _desktopReady.TrySetResult(new DesktopContext(desktop, shell));
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
                    if (returned && !acknowledged) shell.Reset();
                    continue;
                }

                if (message.Id == PrivacyDesktopShellBridge.ShellExitedMessage)
                {
                    if (_originalDesktop != 0 && NativeMethods.SwitchDesktop(_originalDesktop))
                    {
                        MarkReturned("desktop.shell-exited");
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
            shell.Dispose();
            NativeMethods.SetThreadDesktop(threadOriginalDesktop);
            NativeMethods.CloseDesktop(desktop);
        }
    }

    private sealed record DesktopContext(nint Desktop, DesktopShellHost Shell);

    private void MarkReturned(string eventName)
    {
        var wasActive = Volatile.Read(ref _isActive);
        Volatile.Write(ref _isActive, false);
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
