using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Win32;

public sealed partial class WindowVisibilityController
{
    private static HiddenWindowRecord? Capture(WindowInfo window)
    {
        var placement = new NativeMethods.WindowPlacement
        {
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>()
        };
        if (!NativeMethods.GetWindowPlacement((nint)window.Handle, ref placement)) return null;

        var processStartTime = ProcessAccessInspector.GetProcessStartTimeUtcTicks(window.ProcessId);
        if (processStartTime <= 0) return null;

        return new HiddenWindowRecord
        {
            Handle = window.Handle,
            ProcessId = window.ProcessId,
            ProcessStartTimeUtcTicks = processStartTime,
            ExecutablePath = window.ExecutablePath,
            Placement = new WindowPlacementSnapshot
            {
                Flags = (int)placement.Flags,
                ShowCommand = (int)placement.ShowCmd,
                MinPositionX = placement.MinPosition.X,
                MinPositionY = placement.MinPosition.Y,
                MaxPositionX = placement.MaxPosition.X,
                MaxPositionY = placement.MaxPosition.Y,
                Left = placement.NormalPosition.Left,
                Top = placement.NormalPosition.Top,
                Right = placement.NormalPosition.Right,
                Bottom = placement.NormalPosition.Bottom
            },
            WasForeground = NativeMethods.GetForegroundWindow() == (nint)window.Handle
        };
    }

    private WindowIdentityStatus GetWindowIdentity(HiddenWindowRecord record, nint window)
    {
        if (!_nativeActions.Exists(record.Handle)) return WindowIdentityStatus.Different;

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId != record.ProcessId || record.ProcessStartTimeUtcTicks <= 0)
        {
            return WindowIdentityStatus.Different;
        }

        var processStartTime = ProcessAccessInspector.GetProcessStartTimeUtcTicks(record.ProcessId);
        if (processStartTime <= 0) return WindowIdentityStatus.Unknown;
        if (processStartTime != record.ProcessStartTimeUtcTicks) return WindowIdentityStatus.Different;

        var executablePath = WindowCatalog.GetProcessPath(processId);
        if (string.IsNullOrWhiteSpace(executablePath)) return WindowIdentityStatus.Unknown;

        return string.Equals(executablePath, record.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            ? WindowIdentityStatus.Same
            : WindowIdentityStatus.Different;
    }

    private async Task<bool> WaitForVisibilityAsync(
        nint window,
        bool visible,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!_nativeActions.Exists((long)window)) return false;
            if (_nativeActions.IsVisible((long)window) == visible) return true;
            await Task.Delay(25, cancellationToken);
        }

        return _nativeActions.Exists((long)window) && _nativeActions.IsVisible((long)window) == visible;
    }

    private static string FormatResult(VisibilityOperationResult result) =>
        $"changed={result.ChangedCount}; failed={result.FailedCount}; elevated={result.SkippedElevatedCount}";

    private sealed record RestoreOutcome(
        VisibilityOperationResult Result,
        IReadOnlyList<HiddenWindowRecord> Remaining);

    private enum WindowIdentityStatus
    {
        Same,
        Different,
        Unknown
    }
}
