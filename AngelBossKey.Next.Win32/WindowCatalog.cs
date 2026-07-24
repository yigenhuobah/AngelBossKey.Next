using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using System.Diagnostics;
using System.Text;

namespace AngelBossKey.Next.Win32;

public sealed class WindowCatalog : IWindowCatalog
{
    private readonly int _currentProcessId = Environment.ProcessId;

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((window, _) =>
        {
            var candidate = TryGetWindowCore(window, requireVisible: true);
            if (candidate is not null &&
                candidate.ProcessId != _currentProcessId)
            {
                windows.Add(candidate);
            }

            return true;
        }, 0);

        return windows
            .OrderBy(window => window.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public WindowInfo? TryGetWindow(long handle) => TryGetWindowCore((nint)handle, requireVisible: false);

    private static WindowInfo? TryGetWindowCore(nint window, bool requireVisible)
    {
        if (window == 0 ||
            !NativeMethods.IsWindow(window) ||
            (requireVisible && !NativeMethods.IsWindowVisible(window)) ||
            NativeMethods.GetAncestor(window, NativeMethods.GaRoot) != window ||
            IsCloaked(window))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var executablePath = GetProcessPath(processId);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var titleLength = Math.Min(NativeMethods.GetWindowTextLengthW(window), 4096);
        var title = new StringBuilder(titleLength + 1);
        _ = NativeMethods.GetWindowTextW(window, title, title.Capacity);

        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            processName = Path.GetFileNameWithoutExtension(executablePath);
        }

        var displayName = GetDisplayName(executablePath, processName);
        return new WindowInfo
        {
            Handle = window,
            ProcessId = (int)processId,
            Title = title.ToString(),
            ProcessName = processName,
            DisplayName = displayName,
            ExecutablePath = executablePath
        };
    }

    private static bool IsCloaked(nint window)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmwaCloaked,
                out var cloaked,
                sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    internal static string? GetProcessPath(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var capacity = 1024u;
            var path = new StringBuilder((int)capacity);
            return NativeMethods.QueryFullProcessImageName(process, 0, path, ref capacity)
                ? path.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static string GetDisplayName(string executablePath, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return info.FileDescription?.Trim() is { Length: > 0 } description
                ? description
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
