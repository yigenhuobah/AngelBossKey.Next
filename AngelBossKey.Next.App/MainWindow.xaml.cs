using AngelBossKey.Next.App.ViewModels;
using AngelBossKey.Next.Core.Abstractions;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AngelBossKey.Next.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IWindowCatalog _windowCatalog;

    public MainWindow(MainWindowViewModel viewModel, IWindowCatalog windowCatalog)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windowCatalog = windowCatalog;
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        App.Instance.HandleWindowClose(e);
        base.OnClosing(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.RefreshPathValidity();
    }

    private async void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WindowPickerDialog(_windowCatalog) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.AddTargetsAsync(dialog.SelectedWindows);
        }
    }

    private async void SetHotkey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeyDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Gesture is not null)
        {
            await _viewModel.SetHotkeyAsync(dialog.Gesture);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AngelBossKey.Next",
            "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
