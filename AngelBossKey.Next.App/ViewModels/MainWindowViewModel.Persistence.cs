using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private TargetRule[] EffectiveTargets() =>
        Targets.Select(target => target.ToEffectiveModel()).ToArray();

    private async Task PersistTargetsAsync()
    {
        SaveSelectedTargets();
        await PersistAllAsync();
    }

    private async Task PersistAllAsync()
    {
        SaveSelectedTargets();
        SaveSelectedLaunchItems();
        UpdateSettingsSnapshot();
        await QueueSettingsSaveAsync();
    }

    private void UpdateSettingsSnapshot()
    {
        var active = SelectedScene.ToModel();
        _settings = _settings with
        {
            SchemaVersion = 8,
            Scenes = Scenes.Select(scene => scene.ToModel()).ToList(),
            ActiveSceneId = SelectedScene.Id,
            Hotkey = active.Hotkey,
            Targets = active.Targets,
            EnableElevatedBroker = EnableElevatedBroker
        };
    }

    private async Task DrainRuleChangesAsync()
    {
        while (true)
        {
            Task pending;
            lock (_ruleTaskSync) pending = _ruleChangeTail;
            await pending;
            lock (_ruleTaskSync)
            {
                if (ReferenceEquals(pending, _ruleChangeTail)) return;
            }
        }
    }

    private async Task ApplyRuleChangesAsync(string? successMessage)
    {
        await _ruleChangeGate.WaitAsync();
        try
        {
            await PersistTargetsAsync();
            if (_visibilityController.IsHidden &&
                (_operationSceneId is null || _operationSceneId == SelectedScene.Id))
            {
                var rules = EffectiveTargets();
                var result = await _visibilityController.UpdateTargetsAsync(rules);
                await _audioController.ReconcileAsync(rules);
                if (result.ChangedCount > 0 || result.FailedCount > 0)
                {
                    Message = result.FailedCount > 0
                        ? $"已按规则恢复 {result.ChangedCount} 个窗口，{result.FailedCount} 个仍待恢复。"
                        : $"已按规则恢复 {result.ChangedCount} 个窗口。";
                }
            }
            if (successMessage is not null) Message = successMessage;
        }
        catch (Exception exception)
        {
            _diagnosticLog.LogError("rules.save", exception);
            Message = $"保存目标设置失败：{exception.Message}";
        }
        finally { _ruleChangeGate.Release(); }
    }

    private void QueueRuleChanges(string? successMessage = null)
    {
        lock (_ruleTaskSync)
        {
            _ruleChangeTail = ContinueRuleChangesAsync(_ruleChangeTail, successMessage);
        }
    }

    private async Task ContinueRuleChangesAsync(Task previous, string? successMessage)
    {
        try { await previous; }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("rules.queue", $"previous-failure={exception.GetType().Name}");
        }
        await ApplyRuleChangesAsync(successMessage);
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
        try { await PersistAllAsync(); }
        catch (Exception exception) { Message = $"保存设置失败：{exception.Message}"; }
    }

    private async Task SaveAfterAsync(Task previous, AppSettings snapshot)
    {
        try { await previous; }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("settings.queue", $"previous-failure={exception.GetType().Name}");
        }
        await _settingsStore.SaveAsync(snapshot);
    }
}
