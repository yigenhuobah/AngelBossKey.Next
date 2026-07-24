using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

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

    internal static bool IsSameUserAndSession(int processId)
    {
        try
        {
            using var targetProcess = Process.GetProcessById(processId);
            using var currentProcess = Process.GetCurrentProcess();
            if (targetProcess.SessionId != currentProcess.SessionId) return false;

            var targetSid = GetProcessUserSid((uint)processId);
            using var currentIdentity = WindowsIdentity.GetCurrent();
            return targetSid is not null && currentIdentity.User is not null &&
                string.Equals(targetSid, currentIdentity.User.Value, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetProcessUserSid(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token)) return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
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
