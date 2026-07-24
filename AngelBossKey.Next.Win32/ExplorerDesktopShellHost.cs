using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AngelBossKey.Next.Win32;

internal sealed class ExplorerDesktopShellHost(
    nint desktop,
    string desktopName,
    IDiagnosticLog? diagnosticLog = null,
    string? explorerPath = null) : IDisposable
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string DesktopClass = "Progman";
    private readonly object _sync = new();
    private readonly IDiagnosticLog _log = diagnosticLog ?? NullDiagnosticLog.Instance;
    private readonly string _explorerPath = explorerPath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private nint _processHandle;
    private uint _processId;
    private CancellationTokenSource? _monitorShutdown;
    private bool _disposed;

    internal event EventHandler? Exited;

    internal uint ProcessId
    {
        get
        {
            lock (_sync) return _processId;
        }
    }

    internal bool IsReady
    {
        get
        {
            lock (_sync) return IsReadyCore();
        }
    }

    internal static string BuildCommandLine(string applicationPath) => $"\"{applicationPath}\"";

    internal static bool IsRequiredShellClass(string className) =>
        string.Equals(className, TaskbarClass, StringComparison.Ordinal) ||
        string.Equals(className, DesktopClass, StringComparison.Ordinal);

    internal (bool Success, string Message) EnsureReady(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseExitedProcessHandle();
            if (TryAdoptReadyShell())
            {
                StartProcessMonitor(_processHandle, _processId);
                return (true, "完整 Explorer 桌面已就绪。");
            }
            if (_processHandle == 0 && !StartExplorer(out var error)) return (false, error);

            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryAdoptReadyShell())
                {
                    StartProcessMonitor(_processHandle, _processId);
                    _log.Info("desktop.explorer", $"ready=true; pid={_processId}");
                    return (true, "完整 Explorer 桌面已就绪。");
                }
                Thread.Sleep(75);
            }

            var message = "Explorer 未能在独立桌面创建完整任务栏和桌面。";
            _log.Warning("desktop.explorer", $"ready=false; launcherPid={_processId}");
            StopShell();
            return (false, message);
        }
    }

    internal void Stop()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StopShell();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CloseDesktopWindows();
            StopShell();
        }
    }

    private bool StartExplorer(out string error)
    {
        if (!File.Exists(_explorerPath))
        {
            error = "找不到 Windows Explorer，无法创建完整桌面。";
            return false;
        }

        var startup = new NativeMethods.StartupInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.StartupInfo>(),
            Desktop = $"winsta0\\{desktopName}"
        };
        var commandLine = new StringBuilder(BuildCommandLine(_explorerPath));
        if (!NativeMethods.CreateProcess(
                _explorerPath,
                commandLine,
                0,
                0,
                false,
                NativeMethods.CreateUnicodeEnvironment,
                0,
                Path.GetDirectoryName(_explorerPath),
                ref startup,
                out var process))
        {
            error = $"无法在独立桌面启动 Explorer（错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        _processHandle = process.Process;
        _processId = process.ProcessId;
        NativeMethods.CloseHandle(process.Thread);
        error = string.Empty;
        return true;
    }

    private bool TryAdoptReadyShell()
    {
        var shellProcessId = FindCompleteShellProcess();
        if (shellProcessId == 0) return false;
        if (shellProcessId == _processId && IsProcessRunning(_processHandle)) return true;

        var shellProcess = NativeMethods.OpenProcess(
            NativeMethods.Synchronize | NativeMethods.ProcessTerminate,
            false,
            shellProcessId);
        if (shellProcess == 0) return false;
        if (_processHandle != 0) NativeMethods.CloseHandle(_processHandle);
        _processHandle = shellProcess;
        _processId = shellProcessId;
        return true;
    }

    private uint FindCompleteShellProcess()
    {
        var classesByProcess = new Dictionary<uint, ShellWindowKinds>();
        NativeMethods.EnumDesktopWindows(desktop, (window, _) =>
        {
            var className = new StringBuilder(128);
            if (NativeMethods.GetClassNameW(window, className, className.Capacity) == 0) return true;
            var kind = className.ToString() switch
            {
                TaskbarClass => ShellWindowKinds.Taskbar,
                DesktopClass => ShellWindowKinds.Desktop,
                _ => ShellWindowKinds.None
            };
            if (kind == ShellWindowKinds.None) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            classesByProcess.TryGetValue(processId, out var existing);
            classesByProcess[processId] = existing | kind;
            return true;
        }, 0);

        if (_processId != 0 && classesByProcess.TryGetValue(_processId, out var current) &&
            current == ShellWindowKinds.Complete)
        {
            return _processId;
        }
        return classesByProcess.FirstOrDefault(pair => pair.Value == ShellWindowKinds.Complete).Key;
    }

    private bool IsReadyCore() =>
        !_disposed && IsProcessRunning(_processHandle) && FindCompleteShellProcess() == _processId;

    private void StartProcessMonitor(nint processHandle, uint processId)
    {
        _monitorShutdown?.Cancel();
        var shutdown = new CancellationTokenSource();
        _monitorShutdown = shutdown;
        _ = MonitorProcessAsync(processHandle, processId, shutdown.Token);
    }

    private async Task MonitorProcessAsync(
        nint processHandle,
        uint processId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, cancellationToken);
                lock (_sync)
                {
                    if (cancellationToken.IsCancellationRequested || _disposed ||
                        processHandle != _processHandle) return;
                    if (IsProcessRunning(processHandle) && FindCompleteShellProcess() == processId) continue;
                }

                _log.Warning("desktop.explorer.exit", $"pid={processId}");
                Exited?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CloseDesktopWindows()
    {
        NativeMethods.EnumDesktopWindows(desktop, (window, _) =>
        {
            NativeMethods.PostMessageW(window, NativeMethods.WmClose, 0, 0);
            return true;
        }, 0);
    }

    private void CloseOwnedShellWindows()
    {
        NativeMethods.EnumDesktopWindows(desktop, (window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == _processId) NativeMethods.PostMessageW(window, NativeMethods.WmClose, 0, 0);
            return true;
        }, 0);
    }

    private void ReleaseExitedProcessHandle()
    {
        if (_processHandle == 0 || IsProcessRunning(_processHandle)) return;
        NativeMethods.CloseHandle(_processHandle);
        _processHandle = 0;
        _processId = 0;
    }

    private void StopShell()
    {
        _monitorShutdown?.Cancel();
        _monitorShutdown = null;
        if (_processHandle != 0 && IsProcessRunning(_processHandle))
        {
            CloseOwnedShellWindows();
            if (NativeMethods.WaitForSingleObject(_processHandle, 1_500) == NativeMethods.WaitTimeout)
            {
                NativeMethods.TerminateProcess(_processHandle, 0);
                NativeMethods.WaitForSingleObject(_processHandle, 750);
            }
        }
        if (_processHandle != 0) NativeMethods.CloseHandle(_processHandle);
        _processHandle = 0;
        _processId = 0;
    }

    private static bool IsProcessRunning(nint processHandle) =>
        processHandle != 0 &&
        NativeMethods.WaitForSingleObject(processHandle, 0) == NativeMethods.WaitTimeout;

    [Flags]
    private enum ShellWindowKinds
    {
        None = 0,
        Taskbar = 1,
        Desktop = 2,
        Complete = Taskbar | Desktop
    }
}
