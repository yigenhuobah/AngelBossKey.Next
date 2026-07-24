using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AngelBossKey.Next.Win32;

internal sealed class ProcessAccessInspector
{
    private readonly ElevationStatus _currentProcessStatus = GetElevationStatus((uint)Environment.ProcessId);

    internal bool CannotSafelyAccess(int processId)
    {
        if (_currentProcessStatus == ElevationStatus.Elevated)
        {
            return false;
        }

        return GetElevationStatus((uint)processId) != ElevationStatus.NotElevated;
    }

    internal static long GetProcessStartTimeUtcTicks(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static ElevationStatus GetElevationStatus(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return ElevationStatus.Unknown;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
            {
                return ElevationStatus.Unknown;
            }

            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token,
                        NativeMethods.TokenElevation,
                        out var elevation,
                        (uint)Marshal.SizeOf<NativeMethods.TokenElevationData>(),
                        out _))
                {
                    return ElevationStatus.Unknown;
                }

                return elevation.TokenIsElevated != 0
                    ? ElevationStatus.Elevated
                    : ElevationStatus.NotElevated;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private enum ElevationStatus
    {
        Unknown,
        NotElevated,
        Elevated
    }
}
