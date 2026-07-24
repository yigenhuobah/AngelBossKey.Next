using System.Text;

namespace AngelBossKey.Next.Win32;

internal static class ProcessPathResolver
{
    internal static string TryGetPath(int processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            (uint)processId);
        if (process == 0)
        {
            return string.Empty;
        }

        try
        {
            var size = 32768u;
            var buffer = new StringBuilder((int)size);
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size)
                ? buffer.ToString()
                : string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
