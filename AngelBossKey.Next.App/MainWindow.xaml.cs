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
    private bool _synchronizingSceneSelection;
    private int _pendingSceneSelections;
    private long _sceneSelectionVersion;

    public MainWindow(MainWindowViewModel viewModel, IWindowCatalog windowCatalog)
    {
        _viewModel = viewModel;
        _windowCatalog = windowCatalog;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SynchronizeSceneSelection();
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

    private async void SceneSelector_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_synchronizingSceneSelection) return;
        if (sender is not System.Windows.Controls.ComboBox { SelectedItem: SceneRowViewModel scene } selector) return;
        var requestVersion = ++_sceneSelectionVersion;
        _pendingSceneSelections++;
        try
        {
            await _viewModel.SelectSceneAsync(scene);
        }
        finally
        {
            _pendingSceneSelections--;
            if (requestVersion == _sceneSelectionVersion) SynchronizeSceneSelection();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedScene) && _pendingSceneSelections == 0)
        {
            SynchronizeSceneSelection();
        }
    }

    private void SynchronizeSceneSelection()
    {
        _synchronizingSceneSelection = true;
        try { SceneSelector.SelectedItem = _viewModel.SelectedScene; }
        finally { _synchronizingSceneSelection = false; }
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
