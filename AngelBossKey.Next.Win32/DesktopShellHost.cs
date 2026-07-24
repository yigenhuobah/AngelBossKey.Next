using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AngelBossKey.Next.Win32;

internal sealed class DesktopShellHost(
    nint desktop,
    string desktopName,
    uint ownerThreadId,
    IDiagnosticLog? diagnosticLog = null,
    string? applicationPath = null) : IDisposable
{
    private readonly object _sync = new();
    private readonly IDiagnosticLog _log = diagnosticLog ?? NullDiagnosticLog.Instance;
    private readonly string _applicationPath = applicationPath ?? Environment.ProcessPath!;
    private nint _jobHandle;
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

    internal nint ShellWindow
    {
        get
        {
            lock (_sync) return FindUsableWindow();
        }
    }

    internal bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _processHandle != 0 &&
                    NativeMethods.WaitForSingleObject(_processHandle, 0) == NativeMethods.WaitTimeout &&
                    FindUsableWindow() != 0;
            }
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

    internal void Stop()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StopShell();
        }
    }

    internal (bool Success, string Message) EnsureReady(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseExitedProcessHandle();
            if (FindUsableWindow() != 0) return (true, "隐私桌面 Shell 已就绪。");
            if (_processHandle == 0 && !StartShell(out var error))
            {
                return (false, error);
            }

            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FindUsableWindow() != 0)
                {
                    _log.Info("desktop.shell", $"ready=true; pid={_processId}");
                    return (true, "隐私桌面 Shell 已就绪。");
                }
                if (_processHandle != 0 &&
                    NativeMethods.WaitForSingleObject(_processHandle, 0) != NativeMethods.WaitTimeout)
                {
                    break;
                }
                Thread.Sleep(50);
            }

            var message = "隐私桌面 Shell 未能创建可见窗口，已取消切换。";
            _log.Warning("desktop.shell", $"ready=false; pid={_processId}");
            StopShell();
            return (false, message);
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

    internal static string BuildShellCommandLine(string applicationPath, uint threadId) =>
        $"\"{applicationPath}\" --privacy-shell {threadId}";

    private bool StartShell(out string error)
    {
        if (!File.Exists(_applicationPath))
        {
            error = "找不到当前程序，无法初始化隐私桌面 Shell。";
            return false;
        }

        if (!EnsureJob(out error)) return false;

        var startup = new NativeMethods.StartupInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.StartupInfo>(),
            Desktop = $"winsta0\\{desktopName}"
        };
        var commandLine = new StringBuilder(BuildShellCommandLine(_applicationPath, ownerThreadId));
        if (!NativeMethods.CreateProcess(
                _applicationPath,
                commandLine,
                0,
                0,
                false,
                NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateSuspended,
                0,
                Path.GetDirectoryName(_applicationPath),
                ref startup,
                out var process))
        {
            error = $"无法启动隐私桌面 Shell（错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        _processHandle = process.Process;
        _processId = process.ProcessId;
        if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.Process))
        {
            error = $"无法隔离隐私桌面进程（错误 {Marshal.GetLastWin32Error()}）。";
            NativeMethods.TerminateProcess(process.Process, 1);
            NativeMethods.CloseHandle(process.Thread);
            StopShell();
            return false;
        }
        if (NativeMethods.ResumeThread(process.Thread) == uint.MaxValue)
        {
            error = $"无法启动隐私桌面线程（错误 {Marshal.GetLastWin32Error()}）。";
            NativeMethods.CloseHandle(process.Thread);
            StopShell();
            return false;
        }
        NativeMethods.CloseHandle(process.Thread);
        StartProcessMonitor(process.Process, process.ProcessId);
        error = string.Empty;
        return true;
    }

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
                    if (NativeMethods.WaitForSingleObject(processHandle, 0) == NativeMethods.WaitTimeout) continue;
                }

                _log.Warning("desktop.shell.exit", $"pid={processId}");
                Exited?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
            error = $"无法创建隐私桌面进程容器（错误 {Marshal.GetLastWin32Error()}）。";
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

        error = $"无法配置隐私桌面进程容器（错误 {Marshal.GetLastWin32Error()}）。";
        NativeMethods.CloseHandle(_jobHandle);
        _jobHandle = 0;
        return false;
    }

    private nint FindUsableWindow()
    {
        nint found = 0;
        NativeMethods.EnumDesktopWindows(desktop, (window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == _processId)
            {
                found = window;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private void CloseDesktopWindows()
    {
        NativeMethods.EnumDesktopWindows(desktop, (window, _) =>
        {
            NativeMethods.PostMessageW(window, NativeMethods.WmClose, 0, 0);
            return true;
        }, 0);
    }

    private void ReleaseExitedProcessHandle()
    {
        if (_processHandle == 0 ||
            NativeMethods.WaitForSingleObject(_processHandle, 0) == NativeMethods.WaitTimeout) return;
        NativeMethods.CloseHandle(_processHandle);
        _processHandle = 0;
        _processId = 0;
    }

    private void StopShell()
    {
        _monitorShutdown?.Cancel();
        _monitorShutdown = null;
        if (_processHandle != 0 &&
            NativeMethods.WaitForSingleObject(_processHandle, 750) == NativeMethods.WaitTimeout &&
            _jobHandle == 0)
        {
            NativeMethods.TerminateProcess(_processHandle, 0);
            NativeMethods.WaitForSingleObject(_processHandle, 750);
        }
        if (_jobHandle != 0)
        {
            NativeMethods.CloseHandle(_jobHandle);
            _jobHandle = 0;
            if (_processHandle != 0) NativeMethods.WaitForSingleObject(_processHandle, 750);
        }
        if (_processHandle != 0) NativeMethods.CloseHandle(_processHandle);
        _processHandle = 0;
        _processId = 0;
    }
}
