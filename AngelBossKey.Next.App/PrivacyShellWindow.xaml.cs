using AngelBossKey.Next.Win32;
using AngelBossKey.Next.App.Services;
using AngelBossKey.Next.Core.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;

namespace AngelBossKey.Next.App;

public partial class PrivacyShellWindow : Window
{
    private readonly uint _ownerThreadId;
    private readonly DispatcherTimer _clockTimer;
    private HwndSource? _windowSource;
    private bool _returnConfirmed;
    private bool _returnPending;

    public PrivacyShellWindow(uint ownerThreadId, Guid sceneId)
    {
        _ownerThreadId = ownerThreadId;
        InitializeComponent();
        AddFavoriteLaunchers(WorkspaceLaunchCatalog.Load(sceneId).Items);
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher);
        UpdateClock();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource.AddHook(WindowProcedure);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_returnConfirmed && _ownerThreadId != 0)
        {
            e.Cancel = true;
            RequestReturn();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _windowSource?.RemoveHook(WindowProcedure);
        base.OnClosed(e);
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        RequestReturn();
    }

    private void CloseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
            this,
            "关闭工作区会结束其中的程序。请先保存工作。是否继续？",
            "关闭独立工作区",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        if (!PrivacyDesktopShellBridge.RequestCloseWorkspace(_ownerThreadId))
        {
            StatusText.Text = "无法请求关闭，请使用紧急返回热键。";
        }
    }

    private void AddFavoriteLaunchers(IReadOnlyList<WorkspaceLaunchItem> items)
    {
        var insertionIndex = 0;
        foreach (var item in items.Take(4))
        {
            var button = new WpfButton
            {
                Content = item.DisplayName,
                Tag = item,
                Style = (Style)FindResource("ShellButton"),
                ToolTip = $"启动 {item.DisplayName}"
            };
            button.Click += FavoriteLaunch_Click;
            LauncherPanel.Children.Insert(insertionIndex++, button);
        }
    }

    private void FavoriteLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: WorkspaceLaunchItem item }) return;
        StatusText.Text = WorkspaceLaunchCatalog.TryLaunch(item, out var error)
            ? $"已启动 {item.DisplayName}。"
            : $"启动失败：{error}";
    }

    private void RequestReturn()
    {
        if (_returnPending) return;
        var window = new WindowInteropHelper(this).Handle;
        if (!PrivacyDesktopShellBridge.RequestReturn(_ownerThreadId, window))
        {
            StatusText.Text = "无法请求返回，请重试紧急返回热键。";
            return;
        }
        _returnPending = true;
        StatusText.Text = "正在返回原桌面…";
    }

    private nint WindowProcedure(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if ((uint)message == PrivacyDesktopShellBridge.ReturnSucceededMessage)
        {
            handled = true;
            _returnConfirmed = true;
            Dispatcher.BeginInvoke(Close);
        }
        else if ((uint)message == PrivacyDesktopShellBridge.ReturnFailedMessage)
        {
            handled = true;
            _returnPending = false;
            StatusText.Text = "返回失败，请重试或使用紧急返回热键。";
        }
        return 0;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string application }) return;
        try
        {
            if (application == "browse")
            {
                BrowseAndLaunch();
                return;
            }
            var startInfo = application switch
            {
                "notepad" => new ProcessStartInfo("notepad.exe"),
                "calculator" => new ProcessStartInfo("calc.exe"),
                "terminal" => new ProcessStartInfo("cmd.exe"),
                _ => throw new InvalidOperationException("未知启动项。")
            };
            startInfo.UseShellExecute = false;
            using var process = Process.Start(startInfo);
            StatusText.Text = "已启动。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"启动失败：{exception.Message}";
        }
    }

    private void BrowseAndLaunch()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要在隐私桌面运行的程序",
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        using var process = Process.Start(new ProcessStartInfo(dialog.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty
        });
        StatusText.Text = "已启动。";
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("yyyy年M月d日 dddd");
    }
}
