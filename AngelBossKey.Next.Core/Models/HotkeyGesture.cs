namespace AngelBossKey.Next.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record HotkeyGesture
{
    public HotkeyModifiers Modifiers { get; init; }
    public int VirtualKey { get; init; }

    public bool IsConfigured => VirtualKey > 0 && Modifiers != HotkeyModifiers.None;
}
