using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Win32;

public sealed class GlobalHotkeyService : IDisposable
{
    public const int HotkeyMessage = 0x0312;
    private const int PrimaryId = 0xB051;
    private const int SecondaryId = 0xB052;
    private nint _window;
    private int _activeId;
    private HotkeyGesture _current = new();

    public event EventHandler? Pressed;

    public void AttachWindow(nint window) => _window = window;

    public bool TryRegister(HotkeyGesture gesture, out string? error)
    {
        error = null;
        if (_window == 0)
        {
            error = "窗口尚未准备好。";
            return false;
        }

        if (!gesture.IsConfigured)
        {
            error = "请同时选择修饰键和普通按键。";
            return false;
        }

        if (_activeId != 0 && gesture == _current)
        {
            return true;
        }

        var nextId = _activeId == PrimaryId ? SecondaryId : PrimaryId;
        var modifiers = (uint)gesture.Modifiers | NativeMethods.ModNoRepeat;
        if (!NativeMethods.RegisterHotKey(_window, nextId, modifiers, (uint)gesture.VirtualKey))
        {
            error = "该快捷键已被其他程序占用。";
            return false;
        }

        if (_activeId != 0)
        {
            NativeMethods.UnregisterHotKey(_window, _activeId);
        }

        _activeId = nextId;
        _current = gesture;
        return true;
    }

    public void HandleMessage(int message, nint parameter)
    {
        if (message == HotkeyMessage && parameter == _activeId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Unregister()
    {
        if (_window == 0 || _activeId == 0)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_window, _activeId);
        _activeId = 0;
        _current = new HotkeyGesture();
    }

    public void Dispose()
    {
        Unregister();

        GC.SuppressFinalize(this);
    }
}
