using AngelBossKey.Next.Core.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AngelBossKey.Next.Win32;

internal static class DesktopJobProcess
{
    internal static bool TryStart(
        nint job,
        string desktopName,
        string executablePath,
        string arguments,
        string? workingDirectory,
        out nint processHandle,
        out uint processId,
        out string error)
    {
        processHandle = 0;
        processId = 0;
        if (job == 0)
        {
            error = "独立桌面进程容器尚未就绪。";
            return false;
        }

        string path;
        try { path = Path.GetFullPath(executablePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            error = "启动程序路径无效。";
            return false;
        }
        if (!File.Exists(path))
        {
            error = "启动程序不存在。";
            return false;
        }

        var directory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Path.GetDirectoryName(path);
        var startup = new NativeMethods.StartupInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.StartupInfo>(),
            Desktop = $"winsta0\\{desktopName}"
        };
        var commandLine = new StringBuilder(BuildCommandLine(path, arguments));
        if (!NativeMethods.CreateProcess(
            path,
            commandLine,
            0,
            0,
            false,
            NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateSuspended,
            0,
            directory,
            ref startup,
            out var process))
        {
            error = $"无法启动程序（错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(job, process.Process))
            {
                error = $"无法把程序加入独立桌面（错误 {Marshal.GetLastWin32Error()}）。";
                NativeMethods.TerminateProcess(process.Process, 1);
                return false;
            }
            if (NativeMethods.ResumeThread(process.Thread) == uint.MaxValue)
            {
                error = $"无法恢复程序线程（错误 {Marshal.GetLastWin32Error()}）。";
                NativeMethods.TerminateProcess(process.Process, 1);
                return false;
            }

            processHandle = process.Process;
            processId = process.ProcessId;
            process.Process = 0;
            error = string.Empty;
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(process.Thread);
            if (process.Process != 0) NativeMethods.CloseHandle(process.Process);
        }
    }

    internal static string BuildCommandLine(string executablePath, string? arguments)
    {
        var command = $"\"{executablePath}\"";
        return string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments.Trim()}";
    }

    internal static IReadOnlyList<WorkspaceProcessInfo> GetApplications(
        nint job,
        params string[] excludedPaths)
    {
        if (job == 0) return [];
        var exclusions = excludedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryNormalizePath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applications = new Dictionary<string, WorkspaceProcessInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                nint handle = 0;
                try
                {
                    handle = NativeMethods.OpenProcess(
                        NativeMethods.ProcessQueryLimitedInformation,
                        false,
                        (uint)process.Id);
                    if (handle == 0 || !NativeMethods.IsProcessInJob(handle, job, out var inJob) || !inJob)
                    {
                        continue;
                    }
                    var path = TryGetPath(handle);
                    var normalized = TryNormalizePath(path);
                    if (normalized.Length == 0 || exclusions.Contains(normalized)) continue;
                    applications.TryAdd(
                        normalized,
                        new WorkspaceProcessInfo(Path.GetFileNameWithoutExtension(path), path));
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
                {
                }
                finally
                {
                    if (handle != 0) NativeMethods.CloseHandle(handle);
                }
            }
        }

        return applications.Values
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string TryGetPath(nint process)
    {
        var size = 32768u;
        var buffer = new StringBuilder((int)size);
        return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}
