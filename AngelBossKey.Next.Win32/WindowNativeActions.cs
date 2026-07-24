namespace AngelBossKey.Next.Win32;

public interface IWindowNativeActions
{
    bool Exists(long handle);
    bool IsVisible(long handle);
    void RequestShow(long handle, int command);
}

public sealed class WindowNativeActions : IWindowNativeActions
{
    public bool Exists(long handle) => NativeMethods.IsWindow((nint)handle);
    public bool IsVisible(long handle) => NativeMethods.IsWindowVisible((nint)handle);
    public void RequestShow(long handle, int command) =>
        _ = NativeMethods.ShowWindowAsync((nint)handle, command);
}
