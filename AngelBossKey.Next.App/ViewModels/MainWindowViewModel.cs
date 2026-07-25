using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using AngelBossKey.Next.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace AngelBossKey.Next.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IWindowVisibilityController _visibilityController;
    private readonly IStartupRegistration _startupRegistration;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly IApplicationAudioController _audioController;
    private readonly IAutomationTriggerService _automationService;
    private readonly IPrivacyDesktopService _privacyDesktop;
    private readonly IElevatedWindowBroker? _elevatedBroker;
    private readonly IDiagnosticLog _diagnosticLog;
    private readonly object _settingsSaveSync = new();
    private readonly object _ruleTaskSync = new();
    private readonly SemaphoreSlim _hotkeyChangeGate = new(1, 1);
    private readonly SemaphoreSlim _ruleChangeGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Task _settingsSaveTail = Task.CompletedTask;
    private Task _ruleChangeTail = Task.CompletedTask;
    private AppSettings _settings;
    private SceneRowViewModel _selectedScene;
    private Guid? _operationSceneId;
    private string _message = "添加目标程序并设置热键后即可启用。";
    private bool _isBusy;
    private bool _launchAtLogin;
    private bool _closeToTray;
    private bool _automationPaused;
    private bool _enableElevatedBroker;

    public MainWindowViewModel(
        AppSettings settings,
        ISettingsStore settingsStore,
        IWindowVisibilityController visibilityController,
        IStartupRegistration startupRegistration,
        GlobalHotkeyService hotkeyService,
        IDiagnosticLog? diagnosticLog = null,
        IApplicationAudioController? audioController = null,
        IAutomationTriggerService? automationService = null,
        IPrivacyDesktopService? privacyDesktop = null,
        IElevatedWindowBroker? elevatedBroker = null)
    {
        _settingsStore = settingsStore;
        _visibilityController = visibilityController;
        _startupRegistration = startupRegistration;
        _hotkeyService = hotkeyService;
        _diagnosticLog = diagnosticLog ?? NullDiagnosticLog.Instance;
        _audioController = audioController ?? new NullAudioController();
        _automationService = automationService ?? new NullAutomationService();
        _privacyDesktop = privacyDesktop ?? new NullPrivacyDesktopService();
        _elevatedBroker = elevatedBroker;
        _launchAtLogin = settings.LaunchAtLogin;
        _closeToTray = settings.CloseToTray;
        _enableElevatedBroker = settings.EnableElevatedBroker;

        var sceneModels = settings.Scenes.Count > 0
            ? settings.Scenes
            :
            [
                new SceneProfile
                {
                    Name = "默认场景",
                    Hotkey = settings.Hotkey,
                    Targets = settings.Targets
                }
            ];
        foreach (var scene in sceneModels)
        {
            var row = new SceneRowViewModel(scene);
            row.PropertyChanged += OnScenePropertyChanged;
            Scenes.Add(row);
        }

        _selectedScene = Scenes.FirstOrDefault(scene => scene.Id == settings.ActiveSceneId) ?? Scenes[0];
        LoadSelectedTargets();
        LoadSelectedLaunchItems();
        _settings = settings with
        {
            SchemaVersion = 8,
            Scenes = Scenes.Select(scene => scene.ToModel()).ToList(),
            ActiveSceneId = _selectedScene.Id
        };

        RemoveTargetCommand = new RelayCommand(RemoveTarget);
        MoveTargetUpCommand = new RelayCommand(parameter => MoveTarget(parameter, -1));
        MoveTargetDownCommand = new RelayCommand(parameter => MoveTarget(parameter, 1));
        ToggleVisibilityCommand = new RelayCommand(_ => _ = ToggleVisibilityAsync(), _ => CanToggle);
        AddSceneCommand = new AsyncRelayCommand(_ => AddSceneAsync(), onException: ReportSceneCommandException);
        RemoveSceneCommand = new AsyncRelayCommand(
            _ => RemoveSelectedSceneAsync(),
            _ => Scenes.Count > 1,
            ReportSceneCommandException);
        InitializeExtendedFeatures();
        _visibilityController.StateChanged += OnOperationStateChanged;
        _privacyDesktop.StateChanged += OnOperationStateChanged;
    }

    public ObservableCollection<SceneRowViewModel> Scenes { get; } = [];
    public ObservableCollection<TargetRowViewModel> Targets { get; } = [];
    public ObservableCollection<LaunchItemRowViewModel> LaunchItems { get; } = [];
    public IReadOnlyList<MouseTriggerOption> MouseTriggerOptions { get; } =
    [
        new(MouseAutomationTrigger.None, "关闭"),
        new(MouseAutomationTrigger.XButton1, "侧键 1"),
        new(MouseAutomationTrigger.XButton2, "侧键 2"),
        new(MouseAutomationTrigger.MiddleButton, "中键"),
        new(MouseAutomationTrigger.WheelUp, "滚轮向上"),
        new(MouseAutomationTrigger.WheelDown, "滚轮向下")
    ];
    public IReadOnlyList<PrivacyDesktopShellOption> PrivacyDesktopShellOptions { get; } =
    [
        new(PrivacyDesktopShellMode.FullExplorer, "完整桌面（Explorer）"),
        new(PrivacyDesktopShellMode.Compatibility, "兼容桌面（轻量）")
    ];
    public ICommand RemoveTargetCommand { get; }
    public ICommand MoveTargetUpCommand { get; }
    public ICommand MoveTargetDownCommand { get; }
    public ICommand AddSceneCommand { get; }
    public AsyncRelayCommand RemoveSceneCommand { get; }
    public RelayCommand ToggleVisibilityCommand { get; }

    public SceneRowViewModel SelectedScene => _selectedScene;

    public bool IsHidden => _visibilityController.IsHidden || _privacyDesktop.IsActive;
    public bool IsHotkeyConfigured => SelectedScene.Hotkey.IsConfigured;
    public bool CanToggle => !_isBusy &&
        (IsHidden || (IsHotkeyConfigured &&
            (SelectedScene.Mode == SceneMode.PrivacyDesktop || Targets.Any(target => target.EffectiveEnabled))));
    public string HotkeyText => HotkeyFormatter.Format(SelectedScene.Hotkey);
    public string StatusTitle => _privacyDesktop.IsActive
        ? "独立桌面已启用"
        : _visibilityController.IsHidden ? "目标已隐藏" : "保护就绪";
    public string StatusDetail => _privacyDesktop.IsActive
        ? "Ctrl+Alt+Shift+F12 可紧急返回"
        : _visibilityController.IsHidden ? "再次触发当前场景热键可恢复" : "目标窗口保持正常显示";
    public string ToggleText => IsHidden ? "返回 / 恢复" :
        SelectedScene.Mode == SceneMode.PrivacyDesktop ? "进入独立桌面" : "隐藏目标";
    public string TargetCountText
    {
        get
        {
            var invalid = Targets.Count(target => !target.IsPathValid);
            return invalid == 0
                ? $"{Targets.Count} 个目标程序"
                : $"{Targets.Count} 个目标程序 · {invalid} 个路径失效";
        }
    }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (!SetProperty(ref _launchAtLogin, value)) return;
            try
            {
                _startupRegistration.SetEnabled(value, Environment.ProcessPath!);
                _settings = _settings with { LaunchAtLogin = value };
                _ = SaveAndReportAsync();
            }
            catch (Exception exception)
            {
                Message = $"开机启动设置失败：{exception.Message}";
            }
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (!SetProperty(ref _closeToTray, value)) return;
            _settings = _settings with { CloseToTray = value };
            _ = SaveAndReportAsync();
        }
    }

    public bool AutomationPaused
    {
        get => _automationPaused;
        set
        {
            if (SetProperty(ref _automationPaused, value))
            {
                _automationService.IsPaused = value;
                Message = value ? "自动化已临时暂停；场景热键仍然有效。" : "自动化已恢复。";
            }
        }
    }

    public bool EnableElevatedBroker
    {
        get => _enableElevatedBroker;
        set
        {
            if (!SetProperty(ref _enableElevatedBroker, value)) return;
            if (_elevatedBroker is not null) _elevatedBroker.IsEnabled = value;
            _settings = _settings with { EnableElevatedBroker = value };
            _ = SaveAndReportAsync();
        }
    }

    public async Task InitializeAsync()
    {
        var errors = new List<string>();
        foreach (var scene in Scenes.Where(scene => scene.Hotkey.IsConfigured))
        {
            if (!_hotkeyService.TryRegister(scene.Id, scene.Hotkey, out var error))
            {
                errors.Add($"{scene.Name}：{error}");
            }
        }

        try
        {
            if (_settings.LaunchAtLogin && !_startupRegistration.IsEnabledFor(Environment.ProcessPath!))
            {
                _startupRegistration.SetEnabled(true, Environment.ProcessPath!);
            }
        }
        catch (Exception exception)
        {
            errors.Add($"开机启动路径修复失败：{exception.Message}");
        }

        _automationService.Configure(SelectedScene.ToAutomation());
        if (errors.Count > 0) Message = string.Join("；", errors);
        await Task.CompletedTask;
        RefreshAllState();
    }

    public Task InitializeHotkeyAsync() => InitializeAsync();

    public async Task<bool> SetHotkeyAsync(HotkeyGesture gesture)
    {
        await _hotkeyChangeGate.WaitAsync();
        try
        {
            var scene = SelectedScene;
            var previous = scene.Hotkey;
            if (!_hotkeyService.TryRegister(scene.Id, gesture, out var error))
            {
                Message = error ?? "热键注册失败。";
                return false;
            }

            scene.Hotkey = gesture;
            try
            {
                await PersistAllAsync();
            }
            catch (Exception exception)
            {
                scene.Hotkey = previous;
                if (previous.IsConfigured) _hotkeyService.TryRegister(scene.Id, previous, out _);
                else _hotkeyService.Unregister(scene.Id);
                Message = $"保存热键失败，已恢复原设置：{exception.Message}";
                return false;
            }

            Message = $"“{scene.Name}”热键已设置为 {HotkeyFormatter.Format(gesture)}。";
            SceneMenuChanged?.Invoke(this, EventArgs.Empty);
            RefreshAllState();
            return true;
        }
        finally
        {
            _hotkeyChangeGate.Release();
        }
    }

    public async Task<bool> SelectSceneAsync(SceneRowViewModel scene)
    {
        if (!Scenes.Contains(scene)) return false;
        SceneRowViewModel? previousScene = null;
        var gateEntered = false;
        var selectionApplied = false;
        try
        {
            await DrainRuleChangesAsync();
            await _operationGate.WaitAsync();
            gateEntered = true;
            if (scene == _selectedScene) return true;
            if (!Scenes.Contains(scene)) return false;
            previousScene = _selectedScene;
            if (IsHidden)
            {
                var restore = await RestoreCurrentCoreAsync();
                if (IsHidden || restore.FailedCount > 0)
                {
                    Message = "当前场景仍有内容未恢复，已取消场景切换。";
                    OnPropertyChanged(nameof(SelectedScene));
                    return false;
                }
            }
            if (_privacyDesktop.HasWorkspace)
            {
                if (_privacyDesktop.RunningApplicationCount > 0)
                {
                    Message = "独立工作区仍有程序运行。请先返回并使用“关闭工作区”，再切换场景。";
                    OnPropertyChanged(nameof(SelectedScene));
                    return false;
                }
                var close = await _privacyDesktop.CloseWorkspaceAsync();
                if (!close.Success)
                {
                    Message = close.Message;
                    OnPropertyChanged(nameof(SelectedScene));
                    return false;
                }
            }
            SaveSelectedTargets();
            SaveSelectedLaunchItems();
            _selectedScene = scene;
            selectionApplied = true;
            LoadSelectedTargets();
            LoadSelectedLaunchItems();
            _settings = _settings with { ActiveSceneId = scene.Id };
            _automationService.Configure(scene.ToAutomation());
            await PersistAllAsync();
            OnPropertyChanged(nameof(SelectedScene));
            Message = $"已切换到“{scene.Name}”。";
            RefreshAllState();
            return true;
        }
        catch (Exception exception)
        {
            if (selectionApplied && previousScene is not null)
            {
                _selectedScene = previousScene;
                LoadSelectedTargets();
                LoadSelectedLaunchItems();
                TryConfigureAutomation(previousScene, "scene.select.rollback");
                UpdateSettingsSnapshot();
            }
            OnPropertyChanged(nameof(SelectedScene));
            _diagnosticLog.LogError("scene.select", exception);
            Message = $"切换场景失败，已恢复原场景：{exception.Message}";
            RefreshAllState();
            return false;
        }
        finally
        {
            if (gateEntered) _operationGate.Release();
        }
    }

    public async Task ActivateSceneAsync(Guid sceneId)
    {
        var scene = Scenes.FirstOrDefault(item => item.Id == sceneId);
        if (scene is null) return;
        if (IsHidden && _operationSceneId == sceneId)
        {
            await ToggleVisibilityAsync();
            return;
        }

        if (scene != SelectedScene && !await SelectSceneAsync(scene)) return;
        await ToggleVisibilityAsync();
    }

    public async Task HandleAutomationAsync(AutomationTriggerSource source)
    {
        if (AutomationPaused) return;
        if (source == AutomationTriggerSource.Idle && IsHidden) return;
        await ToggleVisibilityAsync();
    }

    public async Task AddTargetsAsync(IEnumerable<WindowInfo> windows)
    {
        var existing = Targets.Select(target => TargetRuleMatcher.NormalizePath(target.ExecutablePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<TargetRowViewModel>();
        foreach (var window in windows)
        {
            var path = TargetRuleMatcher.NormalizePath(window.ExecutablePath);
            if (!existing.Add(path)) continue;
            added.Add(AddTargetRow(new TargetRule { DisplayName = window.DisplayName, ExecutablePath = path }));
        }

        if (added.Count == 0) return;
        try
        {
            await PersistTargetsAsync();
            if (_visibilityController.IsHidden)
            {
                var rules = EffectiveTargets();
                await _visibilityController.UpdateTargetsAsync(rules);
                await _audioController.ReconcileAsync(rules);
            }
            Message = $"已添加 {added.Count} 个目标程序。";
        }
        catch (Exception exception)
        {
            foreach (var row in added)
            {
                row.PropertyChanged -= OnTargetPropertyChanged;
                Targets.Remove(row);
            }
            Message = $"添加目标失败，设置未保存：{exception.Message}";
        }
        RefreshAllState();
    }

    public async Task<VisibilityOperationResult> ToggleVisibilityAsync()
    {
        if (!await _operationGate.WaitAsync(0)) return new VisibilityOperationResult();
        try
        {
            if (_isBusy) return new VisibilityOperationResult();
            if (!IsHidden && !CanToggle)
            {
                Message = !IsHotkeyConfigured ? "请先为当前场景设置热键。" : "没有可用的启用规则。";
                return new VisibilityOperationResult();
            }

            _isBusy = true;
            RefreshAllState();
            if (IsHidden)
            {
                var restoreResult = await RestoreCurrentCoreAsync();
                Message = BuildOperationMessage(restoreResult, true);
                return restoreResult;
            }

            _operationSceneId = SelectedScene.Id;
            if (SelectedScene.Mode == SceneMode.PrivacyDesktop)
            {
                // Helper processes read the persisted scene to build their launcher menus.
                await PersistAllAsync();
                var desktopResult = await _privacyDesktop.EnterAsync(new PrivacyDesktopLaunchRequest
                {
                    SceneId = SelectedScene.Id,
                    SceneName = SelectedScene.Name,
                    ShellMode = SelectedScene.PrivacyShellMode,
                    ShowToolbar = SelectedScene.ShowPrivacyToolbar,
                    LaunchItems = LaunchItems.Select(item => item.ToModel()).ToList()
                });
                if (desktopResult.Success)
                {
                    Message = desktopResult.Message;
                    return new VisibilityOperationResult { ChangedCount = 1, Detail = desktopResult.Message };
                }

                var fallbackRules = EffectiveTargets();
                if (fallbackRules.Any(rule => rule.Enabled))
                {
                    var fallback = await HideWindowsCoreAsync(fallbackRules);
                    var detail = $"{desktopResult.Message} 已回退为普通窗口隐藏。";
                    Message = detail;
                    return fallback with { Detail = detail };
                }

                _operationSceneId = null;
                Message = desktopResult.Message;
                return new VisibilityOperationResult { FailedCount = 1, Detail = desktopResult.Message };
            }

            var rules = EffectiveTargets();
            var hideResult = await HideWindowsCoreAsync(rules);
            Message = BuildOperationMessage(hideResult, false);
            return hideResult;
        }
        catch (Exception exception)
        {
            _diagnosticLog.LogError("operation.toggle", exception);
            Message = $"操作失败：{exception.Message}";
            return new VisibilityOperationResult { FailedCount = 1 };
        }
        finally
        {
            _isBusy = false;
            RefreshAllState();
            _operationGate.Release();
        }
    }

    public async Task RestoreAllAsync()
    {
        await _operationGate.WaitAsync();
        try { await RestoreCurrentCoreAsync(); }
        finally { _operationGate.Release(); }
    }

    public void SetRecoveryResult(VisibilityOperationResult result)
    {
        if (result.FailedCount > 0) Message = $"已恢复 {result.ChangedCount} 个窗口，{result.FailedCount} 个仍待恢复。";
        else if (result.ChangedCount > 0) Message = $"已从上次异常退出中恢复 {result.ChangedCount} 个窗口。";
        if (_audioController.PendingRestoreCount > 0)
        {
            Message = $"{Message} {_audioController.PendingRestoreCount} 个音频会话将在后台继续恢复。".Trim();
        }
    }

    public async Task FlushSettingsAsync()
    {
        while (true)
        {
            Task ruleChanges;
            lock (_ruleTaskSync) ruleChanges = _ruleChangeTail;
            await ruleChanges;
            lock (_ruleTaskSync)
            {
                if (ReferenceEquals(ruleChanges, _ruleChangeTail)) break;
            }
        }
        await _hotkeyChangeGate.WaitAsync();
        _hotkeyChangeGate.Release();
        await _ruleChangeGate.WaitAsync();
        try { await PersistTargetsAsync(); }
        catch (Exception exception) { _diagnosticLog.LogError("settings.flush", exception); }
        finally { _ruleChangeGate.Release(); }
        while (true)
        {
            Task pending;
            lock (_settingsSaveSync) pending = _settingsSaveTail;
            try { await pending; }
            catch (Exception exception) { _diagnosticLog.LogError("settings.save", exception); }
            lock (_settingsSaveSync)
            {
                if (ReferenceEquals(pending, _settingsSaveTail)) break;
            }
        }
    }

    public void RefreshPathValidity()
    {
        var targetChanged = false;
        foreach (var target in Targets) targetChanged |= target.RefreshPathValidity();
        var launchChanged = false;
        foreach (var item in LaunchItems) launchChanged |= item.RefreshPathValidity();
        if (!targetChanged && !launchChanged) return;
        RefreshAllState();
        if (targetChanged)
        {
            QueueRuleChanges(_visibilityController.IsHidden ? "程序路径状态已更新，规则已重新应用。" : null);
        }
    }

    private async Task<VisibilityOperationResult> RestoreCurrentCoreAsync()
    {
        var total = new VisibilityOperationResult();
        if (_privacyDesktop.IsActive)
        {
            var desktop = await _privacyDesktop.ReturnAsync();
            total = total with { ChangedCount = desktop.Success ? 1 : 0, FailedCount = desktop.Success ? 0 : 1, Detail = desktop.Message };
        }
        if (_visibilityController.IsHidden)
        {
            var windows = await _visibilityController.RestoreAsync();
            total = total with
            {
                ChangedCount = total.ChangedCount + windows.ChangedCount,
                FailedCount = total.FailedCount + windows.FailedCount,
                SkippedElevatedCount = windows.SkippedElevatedCount
            };
        }
        await _audioController.RestoreAsync();
        if (_audioController.PendingRestoreCount > 0)
        {
            total = total with
            {
                Detail = $"窗口已恢复；{_audioController.PendingRestoreCount} 个音频会话将在后台继续恢复。"
            };
        }
        if (!_privacyDesktop.IsActive && !_visibilityController.IsHidden) _operationSceneId = null;
        return total;
    }

    private async Task<VisibilityOperationResult> HideWindowsCoreAsync(
        IReadOnlyCollection<TargetRule> rules)
    {
        var result = await _visibilityController.HideAsync(rules);
        await _audioController.MuteAsync(rules);
        return result;
    }

    private async Task AddSceneAsync()
    {
        var scene = new SceneRowViewModel(new SceneProfile { Name = $"场景 {Scenes.Count + 1}" });
        var added = false;
        try
        {
            scene.PropertyChanged += OnScenePropertyChanged;
            Scenes.Add(scene);
            added = true;
            RemoveSceneCommand.RaiseCanExecuteChanged();
            SceneMenuChanged?.Invoke(this, EventArgs.Empty);
            if (await SelectSceneAsync(scene)) return;
        }
        catch (Exception exception)
        {
            _diagnosticLog.LogError("scene.add", exception);
            Message = $"新建场景失败：{exception.Message}";
        }

        if (added)
        {
            scene.PropertyChanged -= OnScenePropertyChanged;
            Scenes.Remove(scene);
            RemoveSceneCommand.RaiseCanExecuteChanged();
            SceneMenuChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RemoveSelectedSceneAsync()
    {
        if (Scenes.Count <= 1) return;
        var gateEntered = false;
        SceneRowViewModel? removed = null;
        var index = -1;
        var removalApplied = false;
        try
        {
            await DrainRuleChangesAsync();
            await _operationGate.WaitAsync();
            gateEntered = true;
            if (IsHidden)
            {
                var restore = await RestoreCurrentCoreAsync();
                if (IsHidden || restore.FailedCount > 0)
                {
                    Message = "当前场景仍有内容未恢复，无法删除。";
                    return;
                }
            }
            if (_privacyDesktop.HasWorkspace)
            {
                if (_privacyDesktop.RunningApplicationCount > 0)
                {
                    Message = "独立工作区仍有程序运行。请先返回并使用“关闭工作区”，再删除场景。";
                    return;
                }
                var close = await _privacyDesktop.CloseWorkspaceAsync();
                if (!close.Success)
                {
                    Message = close.Message;
                    return;
                }
            }

            removed = SelectedScene;
            index = Scenes.IndexOf(removed);
            var replacement = Scenes[index == Scenes.Count - 1 ? index - 1 : index + 1];
            _hotkeyService.Unregister(removed.Id);
            removed.PropertyChanged -= OnScenePropertyChanged;
            Scenes.Remove(removed);
            removalApplied = true;
            SceneMenuChanged?.Invoke(this, EventArgs.Empty);
            _selectedScene = replacement;
            LoadSelectedTargets();
            LoadSelectedLaunchItems();
            _automationService.Configure(replacement.ToAutomation());
            await PersistAllAsync();
            OnPropertyChanged(nameof(SelectedScene));
            RemoveSceneCommand.RaiseCanExecuteChanged();
            Message = $"已删除“{removed.Name}”。";
            RefreshAllState();
        }
        catch (Exception exception)
        {
            if (removalApplied && removed is not null)
            {
                Scenes.Insert(index, removed);
                removed.PropertyChanged += OnScenePropertyChanged;
                _selectedScene = removed;
                LoadSelectedTargets();
                LoadSelectedLaunchItems();
                TryConfigureAutomation(removed, "scene.remove.rollback");
                if (removed.Hotkey.IsConfigured) _hotkeyService.TryRegister(removed.Id, removed.Hotkey, out _);
                UpdateSettingsSnapshot();
                OnPropertyChanged(nameof(SelectedScene));
                SceneMenuChanged?.Invoke(this, EventArgs.Empty);
            }
            _diagnosticLog.LogError("scene.remove", exception);
            Message = $"删除场景失败，已恢复原场景：{exception.Message}";
            RemoveSceneCommand.RaiseCanExecuteChanged();
            RefreshAllState();
        }
        finally
        {
            if (gateEntered) _operationGate.Release();
        }
    }

    private void ReportSceneCommandException(Exception exception)
    {
        _diagnosticLog.LogError("scene.command", exception);
        Message = $"场景操作失败：{exception.Message}";
        RefreshAllState();
    }

    private void TryConfigureAutomation(SceneRowViewModel scene, string eventName)
    {
        try { _automationService.Configure(scene.ToAutomation()); }
        catch (Exception exception) { _diagnosticLog.LogError(eventName, exception); }
    }

    private void OnScenePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SceneRowViewModel.Hotkey) or nameof(SceneRowViewModel.HotkeyText))
        {
            return;
        }
        if (e.PropertyName == nameof(SceneRowViewModel.Name))
        {
            SceneMenuChanged?.Invoke(this, EventArgs.Empty);
        }
        if (sender != SelectedScene) { _ = SaveAndReportAsync(); return; }
        if (e.PropertyName is nameof(SceneRowViewModel.IdleMinutes) or
            nameof(SceneRowViewModel.MouseTrigger) or
            nameof(SceneRowViewModel.EnableLowLevelMouseHook) or
            nameof(SceneRowViewModel.CooldownMilliseconds))
        {
            _automationService.Configure(SelectedScene.ToAutomation());
        }
        _ = SaveAndReportAsync();
        RefreshAllState();
    }

    private void OnOperationStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) RefreshAllState();
        else dispatcher.Invoke(RefreshAllState);
    }

    private void RefreshAllState()
    {
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsHotkeyConfigured));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(TargetCountText));
        RefreshExtendedState();
        ToggleVisibilityCommand.RaiseCanExecuteChanged();
    }

    private static string BuildOperationMessage(VisibilityOperationResult result, bool restoring)
    {
        if (!string.IsNullOrWhiteSpace(result.Detail)) return result.Detail;
        if (restoring)
        {
            var message = result.ChangedCount > 0 ? $"已恢复 {result.ChangedCount} 个窗口。" : "当前没有待恢复窗口。";
            if (result.FailedCount > 0) message += $" {result.FailedCount} 个窗口仍待恢复。";
            return message;
        }
        var text = result.ChangedCount > 0 ? $"已隐藏 {result.ChangedCount} 个窗口。" : "已进入隐藏状态。";
        if (result.SkippedElevatedCount > 0) text += $" {result.SkippedElevatedCount} 个高权限窗口保持可见。";
        if (result.FailedCount > 0) text += $" {result.FailedCount} 个窗口操作失败。";
        return text;
    }

    private sealed class NullAudioController : IApplicationAudioController
    {
        public bool IsActive => false;
        public int PendingRestoreCount => 0;
        public Task<int> MuteAsync(IReadOnlyCollection<TargetRule> targets, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ReconcileAsync(IReadOnlyCollection<TargetRule> targets, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NullAutomationService : IAutomationTriggerService
    {
        public event EventHandler<AutomationTriggeredEventArgs>? Triggered { add { } remove { } }
        public bool IsPaused { get; set; }
        public void Configure(AutomationSettings settings) { }
        public void Dispose() { }
    }

    private sealed class NullPrivacyDesktopService : IPrivacyDesktopService
    {
        public bool IsActive => false;
        public bool HasWorkspace => false;
        public int RunningApplicationCount => 0;
        public PrivacyDesktopShellMode? ActiveShellMode => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<(bool Success, string Message)> EnterAsync(
            PrivacyDesktopLaunchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((false, "独立桌面服务不可用。"));
        public Task<(bool Success, string Message)> ReturnAsync(CancellationToken cancellationToken = default) => Task.FromResult((true, "当前已在原桌面。"));
        public Task<(bool Success, string Message)> CloseWorkspaceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "当前没有独立工作区。"));
        public IReadOnlyList<WorkspaceProcessInfo> GetRunningApplications() => [];
        public void Dispose() { }
    }
}

public sealed record MouseTriggerOption(MouseAutomationTrigger Value, string Label);
public sealed record PrivacyDesktopShellOption(PrivacyDesktopShellMode Value, string Label);
