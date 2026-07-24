using AngelBossKey.Next.Core.Models;
using System.Runtime.InteropServices;

namespace AngelBossKey.Next.Win32;

internal static class WindowPlacementInterop
{
    internal static bool TryCreate(
        WindowPlacementSnapshot? snapshot,
        bool clampToWorkArea,
        out NativeMethods.WindowPlacement placement)
    {
        placement = default;
        if (snapshot is null) return false;
        var width = (long)snapshot.Right - snapshot.Left;
        var height = (long)snapshot.Bottom - snapshot.Top;
        if (width is < 1 or > 100_000 || height is < 1 or > 100_000) return false;

        placement = new NativeMethods.WindowPlacement
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.WindowPlacement>(),
            Flags = (uint)snapshot.Flags,
            ShowCmd = (uint)Math.Clamp(Math.Max(snapshot.ShowCommand, NativeMethods.SwShowNormal), 1, 11),
            MinPosition = new NativeMethods.Point { X = snapshot.MinPositionX, Y = snapshot.MinPositionY },
            MaxPosition = new NativeMethods.Point { X = snapshot.MaxPositionX, Y = snapshot.MaxPositionY },
            NormalPosition = new NativeMethods.Rect
            {
                Left = snapshot.Left,
                Top = snapshot.Top,
                Right = snapshot.Right,
                Bottom = snapshot.Bottom
            }
        };
        if (clampToWorkArea) ClampToVisibleWorkArea(ref placement);
        return true;
    }

    internal static void ClampToVisibleWorkArea(ref NativeMethods.WindowPlacement placement)
    {
        var rectangle = placement.NormalPosition;
        var width = Math.Max(100, rectangle.Right - rectangle.Left);
        var height = Math.Max(80, rectangle.Bottom - rectangle.Top);
        var monitor = NativeMethods.MonitorFromRect(in rectangle, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0) return;

        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo)) return;

        var work = monitorInfo.WorkArea;
        width = Math.Min(width, Math.Max(100, work.Right - work.Left));
        height = Math.Min(height, Math.Max(80, work.Bottom - work.Top));
        var left = Math.Clamp(rectangle.Left, work.Left, Math.Max(work.Left, work.Right - width));
        var top = Math.Clamp(rectangle.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        placement.NormalPosition = new NativeMethods.Rect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
    }
}
