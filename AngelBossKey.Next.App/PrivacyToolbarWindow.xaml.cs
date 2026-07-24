using AngelBossKey.Next.App.Services;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Win32;
using System.Windows;
using System.Windows.Interop;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMessageBox = System.Windows.MessageBox;

namespace AngelBossKey.Next.App;

public partial class PrivacyToolbarWindow : Window
{
    private readonly uint _ownerThreadId;
    private readonly IReadOnlyList<WorkspaceLaunchItem> _launchItems;

    public PrivacyToolbarWindow(uint ownerThreadId, Guid sceneId)
    {
        _ownerThreadId = ownerThreadId;
        InitializeComponent();
        var catalog = WorkspaceLaunchCatalog.Load(sceneId);
        SceneText.Text = catalog.SceneName;
        _launchItems = catalog.Items;
        LaunchButton.IsEnabled = _launchItems.Count > 0;
        Loaded += (_, _) => PositionToolbar();
    }

    private void PositionToolbar()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - ActualWidth - 18);
        Top = workArea.Top + 18;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new WpfContextMenu { PlacementTarget = LaunchButton };
        foreach (var item in _launchItems)
        {
            var menuItem = new WpfMenuItem { Header = item.DisplayName, Tag = item };
            menuItem.Click += LaunchItem_Click;
            menu.Items.Add(menuItem);
        }
        menu.IsOpen = true;
    }

    private void LaunchItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: WorkspaceLaunchItem item }) return;
        if (!WorkspaceLaunchCatalog.TryLaunch(item, out var error))
        {
            WpfMessageBox.Show(this, error, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        var window = new WindowInteropHelper(this).Handle;
        if (!PrivacyDesktopShellBridge.RequestReturn(_ownerThreadId, window))
        {
            WpfMessageBox.Show(this, "无法请求返回，请使用紧急返回热键。", "返回失败");
        }
    }

    private void CloseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show(
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
            WpfMessageBox.Show(this, "无法请求关闭，请使用紧急返回热键。", "关闭失败");
        }
    }
}
