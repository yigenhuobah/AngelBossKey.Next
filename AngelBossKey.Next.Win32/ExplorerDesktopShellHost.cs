using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AngelBossKey.Next.Win32;

internal sealed class ExplorerDesktopShellHost(
    nint desktop,
    string desktopName,
    uint ownerThreadId = 0,
    IDiagnosticLog? diagnosticLog = null,
    string? explorerPath = null,
    string? applicationPath = null) : IDisposable
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string DesktopClass = "Progman";
    private readonly object _sync = new();
    private readonly IDiagnosticLog _log = diagnosticLog ?? NullDiagnosticLog.Instance;
    private readonly string _explorerPath = explorerPath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private readonly string _applicationPath = applicationPath ?? Environment.ProcessPath!;
    private nint _jobHandle;
    private nint _processHandle;
    private uint _processId;
    private nint _toolbarProcessHandle;
    private uint _toolbarProcessId;
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

    internal bool HasWorkspace
    {
        get { lock (_sync) return _jobHandle != 0; }
    }

    internal IReadOnlyList<WorkspaceProcessInfo> GetRunningApplications()
    {
        lock (_sync)
        {
            return DesktopJobProcess.GetApplications(
                _jobHandle,
                _explorerPath,
                _applicationPath);
        }
    }

    internal static string BuildCommandLine(string applicationPath) => $"\"{applicationPath}\"";

    internal static bool IsRequiredShellClass(string className) =>
        string.Equals(className, TaskbarClass, StringComparison.Ordinal) ||
        string.Equals(className, DesktopClass, StringComparison.Ordinal);

    internal static bool IsExpectedExplorerPath(string actualPath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath) || string.IsNullOrWhiteSpace(expectedPath)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal (bool Success, string Message) EnsureReady(
        Guid sceneId,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var hadWorkspace = _jobHandle != 0;
            ReleaseExitedProcessHandle();
            if (TryAdoptReadyShell())
            {
                try
                {
                    if (!EnsureToolbarReady(sceneId, cancellationToken, out var toolbarError))
                    {
                        StopShell(closeWorkspace: !hadWorkspace);
                        return (false, toolbarError);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    StopShell(closeWorkspace: !hadWorkspace);
                    throw;
                }
                StartProcessMonitor(_processHandle, _processId, _toolbarProcessHandle);
                return (true, "完整 Explorer 桌面已就绪。");
            }
            if (_processHandle == 0 && !StartExplorer(out var error)) return (false, error);

            try
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(12))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryAdoptReadyShell())
                    {
                        if (!EnsureToolbarReady(sceneId, cancellationToken, out var toolbarError))
                        {
                            StopShell(closeWorkspace: !hadWorkspace);
                            return (false, toolbarError);
                        }
                        StartProcessMonitor(_processHandle, _processId, _toolbarProcessHandle);
                        _log.Info("desktop.explorer", $"ready=true; pid={_processId}");
                        return (true, "完整 Explorer 桌面已就绪。");
                    }
                    Thread.Sleep(75);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopShell(closeWorkspace: !hadWorkspace);
                throw;
            }

            var message = "Explorer 未能在独立桌面创建完整任务栏和桌面。";
            _log.Warning("desktop.explorer", $"ready=false; launcherPid={_processId}");
            StopShell(closeWorkspace: !hadWorkspace);
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

    internal void Reset()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CloseDesktopWindows();
            StopShell();
        }
    }

    internal (int Started, int Failed) LaunchItems(IEnumerable<WorkspaceLaunchItem> launchItems)
    {
        lock (_sync)
        {
            var started = 0;
            var failed = 0;
            foreach (var item in launchItems.Where(item => item.Enabled))
            {
                if (!DesktopJobProcess.TryStart(
                    _jobHandle,
                    desktopName,
                    item.ExecutablePath,
                    item.Arguments,
                    item.WorkingDirectory,
                    out var handle,
                    out _,
                    out _))
                {
                    failed++;
                    continue;
                }
                NativeMethods.CloseHandle(handle);
                started++;
            }
            return (started, failed);
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

        var hadWorkspace = _jobHandle != 0;
        if (!EnsureJob(out error)) return false;

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
                NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateSuspended,
                0,
                Path.GetDirectoryName(_explorerPath),
                ref startup,
                out var process))
        {
            error = $"无法在独立桌面启动 Explorer（错误 {Marshal.GetLastWin32Error()}）。";
            StopShell(closeWorkspace: !hadWorkspace);
            return false;
        }

        _processHandle = process.Process;
        _processId = process.ProcessId;
        if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.Process))
        {
            error = $"无法隔离独立桌面 Explorer（错误 {Marshal.GetLastWin32Error()}）。";
            NativeMethods.TerminateProcess(process.Process, 1);
            NativeMethods.CloseHandle(process.Thread);
            StopShell(closeWorkspace: !hadWorkspace);
            return false;
        }
        if (NativeMethods.ResumeThread(process.Thread) == uint.MaxValue)
        {
            error = $"无法启动独立桌面 Explorer 线程（错误 {Marshal.GetLastWin32Error()}）。";
            NativeMethods.CloseHandle(process.Thread);
            StopShell(closeWorkspace: !hadWorkspace);
            return false;
        }
        NativeMethods.CloseHandle(process.Thread);
        error = string.Empty;
        return true;
    }

    private bool EnsureJob(out string error)
    {
        if (_jobHandle != 0)
        {
            error = string.Empty;
            return true;
        }

        _jobHandle = NativeMethods.CreateJobObject(0, null);
        if (_jobHandle == 0)
        {
            error = $"无法创建独立桌面进程容器（错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }
        var information = new NativeMethods.JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
            }
        };
        if (NativeMethods.SetInformationJobObject(
            _jobHandle,
            NativeMethods.JobObjectExtendedLimitInformation,
            ref information,
            (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformationData>()))
        {
            error = string.Empty;
            return true;
        }

        error = $"无法配置独立桌面进程容器（错误 {Marshal.GetLastWin32Error()}）。";
        NativeMethods.CloseHandle(_jobHandle);
        _jobHandle = 0;
        return false;
    }

    private bool EnsureToolbarReady(
        Guid sceneId,
        CancellationToken cancellationToken,
        out string error)
    {
        ReleaseExitedToolbarHandle();
        if (IsProcessRunning(_toolbarProcessHandle) && FindVisibleWindow(_toolbarProcessId) != 0)
        {
            error = string.Empty;
            return true;
        }
        if (!File.Exists(_applicationPath))
        {
            error = "找不到当前程序，无法启动独立桌面返回工具条。";
            return false;
        }
        if (!DesktopJobProcess.TryStart(
            _jobHandle,
            desktopName,
            _applicationPath,
            $"--privacy-toolbar {ownerThreadId} {sceneId:D}",
            Path.GetDirectoryName(_applicationPath),
            out _toolbarProcessHandle,
            out _toolbarProcessId,
            out error))
        {
            return false;
        }

        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(6))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessRunning(_toolbarProcessHandle)) break;
            if (FindVisibleWindow(_toolbarProcessId) != 0)
            {
                _log.Info("desktop.toolbar", $"ready=true; pid={_toolbarProcessId}");
                error = string.Empty;
                return true;
            }
            Thread.Sleep(50);
        }

        error = "独立桌面返回工具条未能创建可见窗口。";
        return false;
    }

    private nint FindVisibleWindow(uint processId)
    {
        nint found = 0;
        NativeMethods.EnumDesktopWindows(desktop, (window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != processId) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    private bool TryAdoptReadyShell()
    {
        var shellProcessId = FindCompleteShellProcess();
        if (shellProcessId == 0) return false;
        var processPath = ProcessPathResolver.TryGetPath((int)shellProcessId);
        if (!IsExpectedExplorerPath(processPath, _explorerPath)) return false;
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
        !_disposed && IsProcessRunning(_processHandle) && FindCompleteShellProcess() == _processId &&
        IsProcessRunning(_toolbarProcessHandle) && FindVisibleWindow(_toolbarProcessId) != 0;

    private void StartProcessMonitor(nint processHandle, uint processId, nint toolbarProcessHandle)
    {
        _monitorShutdown?.Cancel();
        var shutdown = new CancellationTokenSource();
        _monitorShutdown = shutdown;
        _ = MonitorProcessAsync(processHandle, processId, toolbarProcessHandle, shutdown.Token);
    }

    private async Task MonitorProcessAsync(
        nint processHandle,
        uint processId,
        nint toolbarProcessHandle,
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
                    if (IsProcessRunning(processHandle) && FindCompleteShellProcess() == processId &&
                        IsProcessRunning(toolbarProcessHandle)) continue;
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

    private void ReleaseExitedToolbarHandle()
    {
        if (_toolbarProcessHandle == 0 || IsProcessRunning(_toolbarProcessHandle)) return;
        NativeMethods.CloseHandle(_toolbarProcessHandle);
        _toolbarProcessHandle = 0;
        _toolbarProcessId = 0;
    }

    private void StopShell(bool closeWorkspace = true)
    {
        _monitorShutdown?.Cancel();
        _monitorShutdown = null;
        if (!closeWorkspace)
        {
            if (_toolbarProcessHandle != 0 && IsProcessRunning(_toolbarProcessHandle))
            {
                NativeMethods.TerminateProcess(_toolbarProcessHandle, 0);
                NativeMethods.WaitForSingleObject(_toolbarProcessHandle, 750);
            }
            if (_processHandle != 0 && IsProcessRunning(_processHandle))
            {
                CloseOwnedShellWindows();
                if (NativeMethods.WaitForSingleObject(_processHandle, 750) == NativeMethods.WaitTimeout)
                {
                    NativeMethods.TerminateProcess(_processHandle, 0);
                    NativeMethods.WaitForSingleObject(_processHandle, 750);
                }
            }
            if (_processHandle != 0) NativeMethods.CloseHandle(_processHandle);
            if (_toolbarProcessHandle != 0) NativeMethods.CloseHandle(_toolbarProcessHandle);
            _processHandle = 0;
            _processId = 0;
            _toolbarProcessHandle = 0;
            _toolbarProcessId = 0;
            return;
        }
        if (_processHandle != 0 && IsProcessRunning(_processHandle))
        {
            CloseOwnedShellWindows();
            NativeMethods.WaitForSingleObject(_processHandle, 1_500);
        }
        if (_jobHandle != 0)
        {
            NativeMethods.CloseHandle(_jobHandle);
            _jobHandle = 0;
            if (_processHandle != 0) NativeMethods.WaitForSingleObject(_processHandle, 750);
        }
        else if (_processHandle != 0 && IsProcessRunning(_processHandle))
        {
            NativeMethods.TerminateProcess(_processHandle, 0);
            NativeMethods.WaitForSingleObject(_processHandle, 750);
        }
        if (_processHandle != 0) NativeMethods.CloseHandle(_processHandle);
        if (_toolbarProcessHandle != 0) NativeMethods.CloseHandle(_toolbarProcessHandle);
        _processHandle = 0;
        _processId = 0;
        _toolbarProcessHandle = 0;
        _toolbarProcessId = 0;
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
