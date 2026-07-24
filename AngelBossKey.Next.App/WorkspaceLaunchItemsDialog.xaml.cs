using AngelBossKey.Next.App.ViewModels;
using System.Windows;

namespace AngelBossKey.Next.App;

public partial class WorkspaceLaunchItemsDialog : Window
{
    private readonly MainWindowViewModel _viewModel;

    public WorkspaceLaunchItemsDialog(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择进入独立工作区时启动的程序",
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.AddLaunchItemAsync(dialog.FileName);
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.CaptureWorkspaceApplicationsAsync();

    private async void CloseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasWorkspace) return;
        if (_viewModel.WorkspaceApplicationCount > 0 && System.Windows.MessageBox.Show(
            this,
            $"工作区仍有 {_viewModel.WorkspaceApplicationCount} 个程序。关闭会结束它们，请先保存工作。是否继续？",
            "关闭独立工作区",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await _viewModel.CloseWorkspaceAsync();
    }
}
