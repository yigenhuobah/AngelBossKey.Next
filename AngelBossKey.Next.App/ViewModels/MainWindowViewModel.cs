using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using AngelBossKey.Next.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace AngelBossKey.Next.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IWindowVisibilityController _visibilityController;
    private readonly IStartupRegistration _startupRegistration;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly object _settingsSaveSync = new();
    private readonly SemaphoreSlim _hotkeyChangeGate = new(1, 1);
    private Task _settingsSaveTail = Task.CompletedTask;
    private AppSettings _settings;
    private string _message = "添加目标程序并设置热键后即可启用。";
    private bool _isBusy;
    private bool _launchAtLogin;
    private bool _closeToTray;

    public MainWindowViewModel(
        AppSettings settings,
        ISettingsStore settingsStore,
        IWindowVisibilityController visibilityController,
        IStartupRegistration startupRegistration,
        GlobalHotkeyService hotkeyService)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _visibilityController = visibilityController;
        _startupRegistration = startupRegistration;
        _hotkeyService = hotkeyService;
        _launchAtLogin = settings.LaunchAtLogin;
        _closeToTray = settings.CloseToTray;

        foreach (var target in settings.Targets)
        {
            AddTargetRow(target);
        }

        RemoveTargetCommand = new RelayCommand(RemoveTarget);
        ToggleVisibilityCommand = new RelayCommand(_ => _ = ToggleVisibilityAsync(), _ => CanToggle);
        _visibilityController.StateChanged += OnVisibilityStateChanged;
    }

    public ObservableCollection<TargetRowViewModel> Targets { get; } = [];
    public ICommand RemoveTargetCommand { get; }
    public RelayCommand ToggleVisibilityCommand { get; }

    public bool IsHidden => _visibilityController.IsHidden;
    public bool IsHotkeyConfigured => _settings.Hotkey.IsConfigured;
    public bool CanToggle => !_isBusy &&
        (IsHidden || (IsHotkeyConfigured && Targets.Any(target => target.Enabled)));
    public string HotkeyText => HotkeyFormatter.Format(_settings.Hotkey);
    public string StatusTitle => IsHidden ? "目标已隐藏" : "保护就绪";
    public string StatusDetail => IsHidden ? "再次触发热键可恢复" : "目标窗口保持正常显示";
    public string ToggleText => IsHidden ? "恢复目标" : "隐藏目标";
    public string TargetCountText => $"{Targets.Count} 个目标程序";
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (!SetProperty(ref _launchAtLogin, value))
            {
                return;
            }

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
            if (!SetProperty(ref _closeToTray, value))
            {
                return;
            }

            _settings = _settings with { CloseToTray = value };
            _ = SaveAndReportAsync();
        }
    }

    public async Task InitializeHotkeyAsync()
    {
        if (_settings.Hotkey.IsConfigured &&
            !_hotkeyService.TryRegister(_settings.Hotkey, out var error))
        {
            Message = error ?? "保存的热键无法注册，请重新设置。";
        }

        try
        {
            if (_settings.LaunchAtLogin &&
                !_startupRegistration.IsEnabledFor(Environment.ProcessPath!))
            {
                _startupRegistration.SetEnabled(true, Environment.ProcessPath!);
            }
        }
        catch (Exception exception)
        {
            Message = $"开机启动路径修复失败：{exception.Message}";
        }

        await Task.CompletedTask;
        RefreshCommandState();
    }

    public async Task<bool> SetHotkeyAsync(HotkeyGesture gesture)
    {
        await _hotkeyChangeGate.WaitAsync();
        try
        {
            var previousHotkey = _settings.Hotkey;
            if (!_hotkeyService.TryRegister(gesture, out var error))
            {
                Message = error ?? "热键注册失败。";
                return false;
            }

            _settings = _settings with { Hotkey = gesture };
            try
            {
                await QueueSettingsSaveAsync();
            }
            catch (Exception exception)
            {
                _settings = _settings with { Hotkey = previousHotkey };
                if (previousHotkey.IsConfigured)
                {
                    if (!_hotkeyService.TryRegister(previousHotkey, out _))
                    {
                        _hotkeyService.Unregister();
                    }
                }
                else
                {
                    _hotkeyService.Unregister();
                }

                try
                {
                    await QueueSettingsSaveAsync();
                    Message = $"保存热键失败，已恢复原设置：{exception.Message}";
                }
                catch (Exception rollbackException)
                {
                    Message = $"保存热键失败，且无法写回原设置：{rollbackException.Message}";
                }

                return false;
            }
            Message = $"热键已设置为 {HotkeyFormatter.Format(gesture)}。";
            OnPropertyChanged(nameof(HotkeyText));
            OnPropertyChanged(nameof(IsHotkeyConfigured));
            RefreshCommandState();
            return true;
        }
        finally
        {
            _hotkeyChangeGate.Release();
        }
    }

    public async Task AddTargetsAsync(IEnumerable<WindowInfo> windows)
    {
        var existing = Targets
            .Select(target => TargetRuleMatcher.NormalizePath(target.ExecutablePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedRows = new List<TargetRowViewModel>();

        foreach (var window in windows)
        {
            var path = TargetRuleMatcher.NormalizePath(window.ExecutablePath);
            if (!existing.Add(path))
            {
                continue;
            }

            addedRows.Add(AddTargetRow(new TargetRule
            {
                DisplayName = window.DisplayName,
                ExecutablePath = path
            }));
        }

        if (addedRows.Count > 0)
        {
            try
            {
                await PersistTargetsAsync();
                Message = $"已添加 {addedRows.Count} 个目标程序。";
            }
            catch (Exception exception)
            {
                foreach (var row in addedRows)
                {
                    row.PropertyChanged -= OnTargetPropertyChanged;
                    Targets.Remove(row);
                }

                _settings = _settings with { Targets = Targets.Select(target => target.ToModel()).ToList() };
                await SaveAndReportAsync();
                Message = $"添加目标失败，设置未保存：{exception.Message}";
            }
        }

        RefreshTargetState();
    }

    public async Task<VisibilityOperationResult> ToggleVisibilityAsync()
    {
        if (_isBusy)
        {
            return new VisibilityOperationResult();
        }

        if (!IsHidden && (!IsHotkeyConfigured || !Targets.Any(target => target.Enabled)))
        {
            Message = !IsHotkeyConfigured ? "请先设置热键。" : "请至少启用一个目标程序。";
            return new VisibilityOperationResult();
        }

        _isBusy = true;
        RefreshCommandState();
        try
        {
            var restoring = IsHidden;
            var result = restoring
                ? await _visibilityController.RestoreAsync()
                : await _visibilityController.HideAsync(Targets.Select(target => target.ToModel()).ToArray());

            Message = BuildOperationMessage(result, restoring);
            return result;
        }
        catch (Exception exception)
        {
            Message = $"操作失败：{exception.Message}";
            return new VisibilityOperationResult { FailedCount = 1 };
        }
        finally
        {
            _isBusy = false;
            RefreshCommandState();
        }
    }

    public void SetRecoveryResult(VisibilityOperationResult result)
    {
        if (result.FailedCount > 0)
        {
            Message = $"已恢复 {result.ChangedCount} 个窗口，{result.FailedCount} 个仍待恢复。";
        }
        else if (result.ChangedCount > 0)
        {
            Message = $"已从上次异常退出中恢复 {result.ChangedCount} 个窗口。";
        }
    }

    public async Task FlushSettingsAsync()
    {
        Task pending;
        lock (_settingsSaveSync)
        {
            pending = _settingsSaveTail;
        }

        try
        {
            await pending;
        }
        catch (Exception exception)
        {
            Message = $"保存设置失败：{exception.Message}";
        }
    }

    private static string BuildOperationMessage(VisibilityOperationResult result, bool restoring)
    {
        if (restoring)
        {
            var message = result.ChangedCount > 0
                ? $"已恢复 {result.ChangedCount} 个窗口。"
                : "当前没有完成恢复的窗口。";
            if (result.FailedCount > 0)
            {
                message += $" {result.FailedCount} 个窗口仍待恢复，恢复记录已保留。";
            }

            return message;
        }

        if (result.ChangedCount == 0 && result.SkippedElevatedCount == 0 && result.FailedCount == 0)
        {
            return "已进入隐藏状态；目标程序的新窗口会自动隐藏。";
        }

        var hideMessage = $"已隐藏 {result.ChangedCount} 个窗口。";
        if (result.SkippedElevatedCount > 0)
        {
            hideMessage += $" {result.SkippedElevatedCount} 个高权限窗口未处理。";
        }
        if (result.FailedCount > 0)
        {
            hideMessage += $" {result.FailedCount} 个窗口操作未确认。";
        }

        return hideMessage;
    }

    private TargetRowViewModel AddTargetRow(TargetRule target)
    {
        var row = new TargetRowViewModel(target);
        row.PropertyChanged += OnTargetPropertyChanged;
        Targets.Add(row);
        return row;
    }

    private void RemoveTarget(object? parameter)
    {
        if (parameter is not TargetRowViewModel target)
        {
            return;
        }

        target.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Remove(target);
        _ = PersistTargetsAndReportAsync($"已移除 {target.DisplayName}。");
        RefreshTargetState();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetRowViewModel.Enabled))
        {
            _ = PersistTargetsAndReportAsync();
            RefreshCommandState();
        }
    }

    private void OnVisibilityStateChanged(object? sender, EventArgs e)
    {
        void UpdateState()
        {
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(StatusTitle));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(ToggleText));
            RefreshCommandState();
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UpdateState();
        }
        else
        {
            dispatcher.Invoke(UpdateState);
        }
    }

    private async Task PersistTargetsAsync()
    {
        _settings = _settings with { Targets = Targets.Select(target => target.ToModel()).ToList() };
        await QueueSettingsSaveAsync();
    }

    private async Task PersistTargetsAndReportAsync(string? successMessage = null)
    {
        try
        {
            await PersistTargetsAsync();
            if (successMessage is not null)
            {
                Message = successMessage;
            }
        }
        catch (Exception exception)
        {
            Message = $"保存目标设置失败：{exception.Message}";
        }
    }

    private Task QueueSettingsSaveAsync()
    {
        var snapshot = _settings;
        lock (_settingsSaveSync)
        {
            _settingsSaveTail = SaveAfterAsync(_settingsSaveTail, snapshot);
            return _settingsSaveTail;
        }
    }

    private async Task SaveAndReportAsync()
    {
        try
        {
            await QueueSettingsSaveAsync();
        }
        catch (Exception exception)
        {
            Message = $"保存设置失败：{exception.Message}";
        }
    }

    private async Task SaveAfterAsync(Task previous, AppSettings snapshot)
    {
        try
        {
            await previous;
        }
        catch
        {
            // A later snapshot can still succeed after an earlier write failed.
        }

        await _settingsStore.SaveAsync(snapshot);
    }

    private void RefreshTargetState()
    {
        OnPropertyChanged(nameof(TargetCountText));
        RefreshCommandState();
    }

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanToggle));
        ToggleVisibilityCommand.RaiseCanExecuteChanged();
    }
}
