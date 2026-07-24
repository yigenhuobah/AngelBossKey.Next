namespace AngelBossKey.Next.Win32;

public static class PrivacyDesktopShellBridge
{
    public const uint ReturnRequestMessage = 0x8051;
    public const uint ReturnSucceededMessage = 0x8052;
    public const uint ReturnFailedMessage = 0x8053;
    internal const uint ShellExitedMessage = 0x8054;
    public const uint CloseWorkspaceRequestMessage = 0x8055;

    public static bool RequestReturn(uint ownerThreadId, nint shellWindow) =>
        ownerThreadId != 0 && shellWindow != 0 &&
        NativeMethods.PostThreadMessageW(ownerThreadId, ReturnRequestMessage, (nuint)shellWindow, 0);

    public static bool RequestCloseWorkspace(uint ownerThreadId) =>
        ownerThreadId != 0 &&
        NativeMethods.PostThreadMessageW(ownerThreadId, CloseWorkspaceRequestMessage, 0, 0);
}
