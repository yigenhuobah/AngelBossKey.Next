using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Models;
using System.Windows;
using System.Windows.Input;

namespace AngelBossKey.Next.App;

public partial class HotkeyDialog : Window
{
    public HotkeyDialog()
    {
        InitializeComponent();
    }

    public HotkeyGesture? Gesture { get; private set; }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin or Key.None)
        {
            GestureText.Text = "继续按下普通按键…";
            return;
        }

        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);
        if (modifiers == HotkeyModifiers.None)
        {
            Gesture = null;
            GestureText.Text = "需要 Ctrl、Alt、Shift 或 Win 修饰键";
            SaveButton.IsEnabled = false;
            return;
        }

        Gesture = new HotkeyGesture
        {
            Modifiers = modifiers,
            VirtualKey = KeyInterop.VirtualKeyFromKey(key)
        };
        GestureText.Text = HotkeyFormatter.Format(Gesture);
        SaveButton.IsEnabled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Gesture is not null)
        {
            DialogResult = true;
        }
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }
}
