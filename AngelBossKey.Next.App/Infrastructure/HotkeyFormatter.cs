using AngelBossKey.Next.Core.Models;
using System.Windows.Input;

namespace AngelBossKey.Next.App.Infrastructure;

public static class HotkeyFormatter
{
    public static string Format(HotkeyGesture gesture)
    {
        if (!gesture.IsConfigured)
        {
            return "未设置";
        }

        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey(gesture.VirtualKey);
        parts.Add(key switch
        {
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            _ => key.ToString()
        });
        return string.Join(" + ", parts);
    }
}
