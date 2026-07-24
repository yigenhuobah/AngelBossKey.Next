using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Storage;
using System.ComponentModel;
using System.IO;

namespace AngelBossKey.Next.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _suppressRuleChanges;
    private CancellationTokenSource? _sceneTestCancellation;
    private int _workspaceApplicationCount;

    public AsyncRelayCommand DuplicateSceneCommand { get; private set; } = null!;
    public AsyncRelayCommand CloseWorkspaceCommand { get; private set; } = null!;
    public AsyncRelayCommand TestSceneCommand { get; private set; } = null!;
    public RelayCommand RemoveLaunchItemCommand { get; private set; } = null!;
    public event EventHandler? SceneMenuChanged;

    public bool HasWorkspace => _privacyDesktop.HasWorkspace;
    public int WorkspaceApplicationCount => _workspaceApplicationCount;
    public bool CanEditWorkspaceSettings => !HasWorkspace;
    public bool CanEditWorkspaceMode => SelectedScene.IsPrivacyDesktop && !HasWorkspace;
    public string WorkspaceStatusText => !HasWorkspace
        ? "工作区尚未启动"
        : WorkspaceApplicationCount == 0
            ? "工作区已保留，没有检测到应用程序"
            : $"工作区已保留 · {WorkspaceApplicationCount} 个程序";
    public string LaunchItemCountText
    {
        get
        {
            var invalid = LaunchItems.Count(item => !item.IsPathValid);
            return invalid == 0
                ? $"{LaunchItems.Count} 个启动项"
                : $"{LaunchItems.Count} 个启动项 · {invalid} 个路径失效";
        }
    }
    public string HotkeyOverviewText => string.Join(
        Environment.NewLine,
        Scenes.Select(scene => $"{scene.Name}：{scene.HotkeyText}"));

    private void InitializeExtendedFeatures()
    {
        DuplicateSceneCommand = new AsyncRelayCommand(
            _ => DuplicateSelectedSceneAsync(),
            onException: ReportSceneCommandException);
        CloseWorkspaceCommand = new AsyncRelayCommand(
            _ => CloseWorkspaceAsync(),
            _ => HasWorkspace,
            ReportSceneCommandException);
        TestSceneCommand = new AsyncRelayCommand(
            _ => TestSceneAsync(TimeSpan.FromSeconds(5)),
            _ => !IsHidden && CanToggle,
            ReportSceneCommandException);
        RemoveLaunchItemCommand = new RelayCommand(RemoveLaunchItem);
    }

    public string ExportSelectedScene()
    {
        SaveSelectedTargets();
        SaveSelectedLaunchItems();
        return SceneProfileTransfer.Export(SelectedScene.ToModel());
    }

    public async Task<bool> ImportSceneAsync(string json)
    {
        var scene = SceneProfileTransfer.Import(json);
        var message = scene.LaunchItems.Count == 0
            ? $"已导入“{scene.Name}”。"
            : $"已导入“{scene.Name}”；其中 {scene.LaunchItems.Count} 个启动项已安全禁用，请复核后手动启用。";
        return await AddSceneModelAsync(scene, message);
    }

    public async Task DuplicateSelectedSceneAsync()
    {
        SaveSelectedTargets();
        SaveSelectedLaunchItems();
        var source = SelectedScene.ToModel();
        var duplicate = source with
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name}（副本）",
            Hotkey = new HotkeyGesture(),
            Targets = [.. source.Targets],
            LaunchItems = source.LaunchItems
                .Select(item => item with { Id = Guid.NewGuid() })
                .ToList()
        };
        await AddSceneModelAsync(duplicate, $"已复制“{source.Name}”，请为副本设置热键。");
    }

    public async Task AddLaunchItemAsync(string executablePath)
    {
        var path = Path.GetFullPath(executablePath);
        if (LaunchItems.Any(item =>
            string.Equals(item.ExecutablePath, path, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(item.Arguments)))
        {
            Message = "该程序已经在启动项中。";
            return;
        }

        var row = AddLaunchItemRow(new WorkspaceLaunchItem
        {
            DisplayName = Path.GetFileNameWithoutExtension(path),
            ExecutablePath = path,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
        });
        try
        {
            await PersistAllAsync();
            Message = $"已添加工作区启动项“{row.DisplayName}”。";
        }
        catch
        {
            row.PropertyChanged -= OnLaunchItemPropertyChanged;
            LaunchItems.Remove(row);
            throw;
        }
        RefreshAllState();
    }

    public async Task CaptureWorkspaceApplicationsAsync()
    {
        var applications = _privacyDesktop.GetRunningApplications();
        var existing = LaunchItems.Select(item => item.ExecutablePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var application in applications)
        {
            if (!existing.Add(application.ExecutablePath)) continue;
            AddLaunchItemRow(new WorkspaceLaunchItem
            {
                DisplayName = application.DisplayName,
                ExecutablePath = application.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(application.ExecutablePath) ?? string.Empty
            });
            added++;
        }
        if (added > 0) await PersistAllAsync();
        Message = added == 0 ? "没有发现新的工作区程序。" : $"已捕获 {added} 个工作区程序。";
        RefreshAllState();
    }

    public async Task CloseWorkspaceAsync()
    {
        var result = await _privacyDesktop.CloseWorkspaceAsync();
        Message = result.Message;
        RefreshAllState();
    }

    public async Task TestSceneAsync(TimeSpan duration)
    {
        _sceneTestCancellation?.Cancel();
        _sceneTestCancellation?.Dispose();
        _sceneTestCancellation = new CancellationTokenSource();
        var cancellationToken = _sceneTestCancellation.Token;
        var result = await ToggleVisibilityAsync();
        if (result.ChangedCount == 0 || result.FailedCount > 0) return;

        try
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
            for (var remaining = seconds; remaining > 0 && IsHidden; remaining--)
            {
                Message = $"场景测试中，将在 {remaining} 秒后自动返回。";
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            if (IsHidden) await ToggleVisibilityAsync();
            Message = "场景测试完成。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task ApplyTargetBatchAsync(
        IReadOnlyCollection<TargetRowViewModel> selection,
        TargetBatchAction action)
    {
        if (selection.Count == 0)
        {
            Message = "请先选择至少一条规则。";
            return;
        }
        var rows = selection;
        _suppressRuleChanges = true;
        try
        {
            foreach (var row in rows)
            {
                switch (action)
                {
                    case TargetBatchAction.Enable: row.Enabled = true; break;
                    case TargetBatchAction.Disable: row.Enabled = false; break;
                    case TargetBatchAction.Exclude: row.TemporarilyExcluded = true; break;
                    case TargetBatchAction.Include: row.TemporarilyExcluded = false; break;
                    case TargetBatchAction.Mute: row.MuteWhenHidden = true; break;
                    case TargetBatchAction.Unmute: row.MuteWhenHidden = false; break;
                    case TargetBatchAction.Remove:
                        row.PropertyChanged -= OnTargetPropertyChanged;
                        Targets.Remove(row);
                        break;
                }
            }
        }
        finally { _suppressRuleChanges = false; }

        await ApplyRuleChangesAsync($"已批量更新 {rows.Count} 条规则。");
        RefreshAllState();
    }

    private async Task<bool> AddSceneModelAsync(SceneProfile model, string message)
    {
        var row = new SceneRowViewModel(model);
        row.PropertyChanged += OnScenePropertyChanged;
        Scenes.Add(row);
        RemoveSceneCommand.RaiseCanExecuteChanged();
        SceneMenuChanged?.Invoke(this, EventArgs.Empty);
        if (await SelectSceneAsync(row))
        {
            Message = message;
            return true;
        }
        row.PropertyChanged -= OnScenePropertyChanged;
        Scenes.Remove(row);
        RemoveSceneCommand.RaiseCanExecuteChanged();
        SceneMenuChanged?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private void LoadSelectedLaunchItems()
    {
        foreach (var row in LaunchItems) row.PropertyChanged -= OnLaunchItemPropertyChanged;
        LaunchItems.Clear();
        foreach (var item in _selectedScene.LaunchItems) AddLaunchItemRow(item);
    }

    private LaunchItemRowViewModel AddLaunchItemRow(WorkspaceLaunchItem item)
    {
        var row = new LaunchItemRowViewModel(item);
        row.PropertyChanged += OnLaunchItemPropertyChanged;
        LaunchItems.Add(row);
        return row;
    }

    private void RemoveLaunchItem(object? parameter)
    {
        if (parameter is not LaunchItemRowViewModel item) return;
        item.PropertyChanged -= OnLaunchItemPropertyChanged;
        LaunchItems.Remove(item);
        _ = SaveAndReportAsync();
        Message = $"已移除启动项“{item.DisplayName}”。";
        RefreshAllState();
    }

    private void OnLaunchItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchItemRowViewModel.IsPathValid) or
            nameof(LaunchItemRowViewModel.StatusText)) return;
        _ = SaveAndReportAsync();
        RefreshAllState();
    }

    private void RefreshExtendedState()
    {
        _workspaceApplicationCount = _privacyDesktop.HasWorkspace
            ? _privacyDesktop.RunningApplicationCount
            : 0;
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(WorkspaceApplicationCount));
        OnPropertyChanged(nameof(CanEditWorkspaceSettings));
        OnPropertyChanged(nameof(CanEditWorkspaceMode));
        OnPropertyChanged(nameof(WorkspaceStatusText));
        OnPropertyChanged(nameof(LaunchItemCountText));
        OnPropertyChanged(nameof(HotkeyOverviewText));
        CloseWorkspaceCommand?.RaiseCanExecuteChanged();
        TestSceneCommand?.RaiseCanExecuteChanged();
    }

    public void RefreshWorkspaceState() => RefreshAllState();
}

public enum TargetBatchAction
{
    Enable,
    Disable,
    Exclude,
    Include,
    Mute,
    Unmute,
    Remove
}
