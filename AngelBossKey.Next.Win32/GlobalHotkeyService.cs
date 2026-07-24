using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Win32;

public sealed class HotkeyPressedEventArgs(Guid registrationId) : EventArgs
{
    public Guid RegistrationId { get; } = registrationId;
}

public sealed class GlobalHotkeyService : IDisposable
{
    public const int HotkeyMessage = 0x0312;
    private const int FirstId = 0xB000;
    private const int LastId = 0xB7FF;
    private readonly Dictionary<Guid, Registration> _registrations = [];
    private readonly Dictionary<int, Guid> _ids = [];
    private nint _window;
    private int _nextId = FirstId;

    public event EventHandler? Pressed;
    public event EventHandler<HotkeyPressedEventArgs>? RegistrationPressed;

    public void AttachWindow(nint window) => _window = window;

    public bool TryRegister(HotkeyGesture gesture, out string? error) =>
        TryRegister(Guid.Empty, gesture, out error);

    public bool TryRegister(Guid registrationId, HotkeyGesture gesture, out string? error)
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

        if (_registrations.TryGetValue(registrationId, out var existing) && existing.Gesture == gesture)
        {
            return true;
        }

        var id = AllocateId();
        if (id == 0)
        {
            error = "可用的热键注册槽已耗尽。";
            return false;
        }

        var modifiers = (uint)gesture.Modifiers | NativeMethods.ModNoRepeat;
        if (!NativeMethods.RegisterHotKey(_window, id, modifiers, (uint)gesture.VirtualKey))
        {
            error = "该快捷键已被其他程序或场景占用。";
            return false;
        }

        if (existing is not null)
        {
            NativeMethods.UnregisterHotKey(_window, existing.Id);
            _ids.Remove(existing.Id);
        }

        _registrations[registrationId] = new Registration(id, gesture);
        _ids[id] = registrationId;
        return true;
    }

    public void HandleMessage(int message, nint parameter)
    {
        if (message != HotkeyMessage || !_ids.TryGetValue((int)parameter, out var registrationId))
        {
            return;
        }

        if (registrationId == Guid.Empty)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        RegistrationPressed?.Invoke(this, new HotkeyPressedEventArgs(registrationId));
    }

    public void Unregister() => Unregister(Guid.Empty);

    public void Unregister(Guid registrationId)
    {
        if (_window == 0 || !_registrations.Remove(registrationId, out var registration))
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_window, registration.Id);
        _ids.Remove(registration.Id);
    }

    public void UnregisterAll()
    {
        foreach (var registration in _registrations.Values)
        {
            if (_window != 0)
            {
                NativeMethods.UnregisterHotKey(_window, registration.Id);
            }
        }

        _registrations.Clear();
        _ids.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        GC.SuppressFinalize(this);
    }

    private int AllocateId()
    {
        for (var count = 0; count <= LastId - FirstId; count++)
        {
            var candidate = _nextId++;
            if (_nextId > LastId)
            {
                _nextId = FirstId;
            }

            if (!_ids.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return 0;
    }

    private sealed record Registration(int Id, HotkeyGesture Gesture);
}
