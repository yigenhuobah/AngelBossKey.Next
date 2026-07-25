using AngelBossKey.Next.Core.Services;
using System.Runtime.InteropServices;

namespace AngelBossKey.Next.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task RunSelfCheckAsync()
    {
        if (!await _operationGate.WaitAsync(0))
        {
            Message = "当前操作尚未完成，稍后再运行自检。";
            return;
        }

        try
        {
            if (_isBusy) return;
            _isBusy = true;
            RefreshAllState();

            var result = await _visibilityController.SelfCheckAsync();
            _diagnosticLog.Info(
                "diagnostics.self-check",
                $"changed={result.ChangedCount}; failed={result.FailedCount}");
            Message = result.FailedCount > 0
                ? $"自检完成：已修正 {result.ChangedCount} 项，仍有 {result.FailedCount} 项待处理。"
                : result.ChangedCount > 0
                    ? $"自检完成：已修正 {result.ChangedCount} 项状态。"
                    : "自检完成：未发现需要修正的窗口状态。";
        }
        catch (Exception exception)
        {
            _diagnosticLog.LogError("diagnostics.self-check", exception);
            Message = $"自检失败：{exception.Message}";
        }
        finally
        {
            _isBusy = false;
            RefreshAllState();
            _operationGate.Release();
        }
    }

    public string BuildDiagnosticReport() => DiagnosticReportFormatter.Format(new DiagnosticReportSnapshot
    {
        AppVersion = typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        OperatingSystem = Environment.OSVersion.VersionString,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        SceneMode = SelectedScene.Mode,
        PrivacyShellMode = SelectedScene.PrivacyShellMode,
        SceneCount = Scenes.Count,
        TargetRuleCount = Targets.Count,
        EnabledTargetRuleCount = Targets.Count(target => target.EffectiveEnabled),
        WindowsHidden = _visibilityController.IsHidden,
        PrivacyDesktopActive = _privacyDesktop.IsActive,
        PrivacyWorkspaceOpen = _privacyDesktop.HasWorkspace,
        AudioActive = _audioController.IsActive,
        PendingAudioRestores = _audioController.PendingRestoreCount,
        ElevatedBrokerEnabled = EnableElevatedBroker
    });

    public void ReportDiagnosticCopied()
    {
        _diagnosticLog.Info("diagnostics.copy", "success=true");
        Message = "已复制脱敏诊断信息。";
    }
}
