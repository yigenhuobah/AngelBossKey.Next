using AngelBossKey.Next.App.ViewModels;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Storage;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMessageBox = System.Windows.MessageBox;

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
        _viewModel.RefreshWorkspaceState();
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

    private void WorkspaceItems_Click(object sender, RoutedEventArgs e) =>
        new WorkspaceLaunchItemsDialog(_viewModel) { Owner = this }.ShowDialog();

    private void SceneActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void DuplicateScene_Click(object sender, RoutedEventArgs e) =>
        _viewModel.DuplicateSceneCommand.Execute(null);

    private async void ImportScene_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入场景",
            Filter = "天使老板键场景 (*.angel-scene.json)|*.angel-scene.json|JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ImportSceneAsync(await ReadSceneFileAsync(dialog.FileName)); }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportScene_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出场景",
            Filter = "天使老板键场景 (*.angel-scene.json)|*.angel-scene.json",
            FileName = $"{SanitizeFileName(_viewModel.SelectedScene.Name)}.angel-scene.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, _viewModel.ExportSelectedScene()); }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HotkeyOverview_Click(object sender, RoutedEventArgs e) => WpfMessageBox.Show(
        this,
        _viewModel.HotkeyOverviewText,
        "场景热键总览",
        MessageBoxButton.OK,
        MessageBoxImage.Information);

    private void TestScene_Click(object sender, RoutedEventArgs e) =>
        _viewModel.TestSceneCommand.Execute(null);

    private async void BatchTargets_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: string actionText } ||
            !Enum.TryParse<TargetBatchAction>(actionText, out var action)) return;
        var rows = TargetGrid.SelectedItems.OfType<TargetRowViewModel>().ToList();
        if (action == TargetBatchAction.Remove && rows.Count > 0 && WpfMessageBox.Show(
            this,
            $"确定移除选中的 {rows.Count} 条规则吗？",
            "批量移除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await _viewModel.ApplyTargetBatchAsync(rows, action);
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

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_viewModel.BuildDiagnosticReport());
            _viewModel.ReportDiagnosticCopied();
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(
                this,
                exception.Message,
                "复制诊断信息失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static async Task<string> ReadSceneFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);
        if (stream.Length > SceneProfileTransfer.MaximumImportBytes)
        {
            throw new InvalidDataException("场景文件超过 1 MB，已拒绝导入。");
        }
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }
}
