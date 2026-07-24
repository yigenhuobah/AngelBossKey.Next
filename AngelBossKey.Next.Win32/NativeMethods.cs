using System.Runtime.InteropServices;
using System.Text;

namespace AngelBossKey.Next.Win32;

internal static partial class NativeMethods
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevation = 20;
    internal const uint EventObjectDestroy = 0x8001;
    internal const uint EventObjectShow = 0x8002;
    internal const uint WineventOutOfContext = 0x0000;
    internal const int ObjidWindow = 0;
    internal const uint GaRoot = 2;
    internal const uint DwmwaCloaked = 14;
    internal const uint ModNoRepeat = 0x4000;
    internal const int SwHide = 0;
    internal const int SwShowNormal = 1;

    internal delegate bool EnumWindowsProc(nint window, nint parameter);
    internal delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        internal uint Length;
        internal uint Flags;
        internal uint ShowCmd;
        internal Point MinPosition;
        internal Point MaxPosition;
        internal Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevationData
    {
        internal int TokenIsElevated;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint window, StringBuilder text, int maximumCount);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowPlacement(nint window, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPlacement(nint window, in WindowPlacement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindowAsync(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll")]
    internal static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out int value,
        uint valueSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        out TokenElevationData tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);
}
