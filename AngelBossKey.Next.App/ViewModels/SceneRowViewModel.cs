using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.App.ViewModels;

public sealed class SceneRowViewModel : ObservableObject
{
    private string _name;
    private HotkeyGesture _hotkey;
    private SceneMode _mode;
    private PrivacyDesktopShellMode _privacyShellMode;
    private int _idleMinutes;
    private MouseAutomationTrigger _mouseTrigger;
    private bool _enableLowLevelMouseHook;
    private int _cooldownMilliseconds;
    private List<TargetRule> _targets;
    private List<WorkspaceLaunchItem> _launchItems;

    public SceneRowViewModel(SceneProfile scene)
    {
        Id = scene.Id;
        _name = scene.Name;
        _hotkey = scene.Hotkey;
        _mode = scene.Mode;
        _privacyShellMode = scene.PrivacyShellMode;
        _targets = [.. scene.Targets];
        _launchItems = [.. scene.LaunchItems];
        _idleMinutes = scene.Automation.IdleMinutes;
        _mouseTrigger = scene.Automation.MouseTrigger;
        _enableLowLevelMouseHook = scene.Automation.EnableLowLevelMouseHook;
        _cooldownMilliseconds = scene.Automation.CooldownMilliseconds;
    }

    public Guid Id { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }
    public HotkeyGesture Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetProperty(ref _hotkey, value))
            {
                OnPropertyChanged(nameof(HotkeyText));
            }
        }
    }
    public string HotkeyText => HotkeyFormatter.Format(Hotkey);
    public SceneMode Mode { get => _mode; set => SetProperty(ref _mode, value); }
    public PrivacyDesktopShellMode PrivacyShellMode
    {
        get => _privacyShellMode;
        set => SetProperty(ref _privacyShellMode, value);
    }
    public bool IsPrivacyDesktop
    {
        get => Mode == SceneMode.PrivacyDesktop;
        set
        {
            if (value != IsPrivacyDesktop)
            {
                Mode = value ? SceneMode.PrivacyDesktop : SceneMode.HideWindows;
                OnPropertyChanged();
            }
        }
    }
    public int IdleMinutes { get => _idleMinutes; set => SetProperty(ref _idleMinutes, Math.Clamp(value, 0, 1440)); }
    public MouseAutomationTrigger MouseTrigger { get => _mouseTrigger; set => SetProperty(ref _mouseTrigger, value); }
    public bool EnableLowLevelMouseHook { get => _enableLowLevelMouseHook; set => SetProperty(ref _enableLowLevelMouseHook, value); }
    public int CooldownMilliseconds
    {
        get => _cooldownMilliseconds;
        set => SetProperty(ref _cooldownMilliseconds, Math.Clamp(value, 250, 60_000));
    }

    public IReadOnlyList<TargetRule> Targets => _targets;
    public void SetTargets(IEnumerable<TargetRule> targets) => _targets = [.. targets];
    public IReadOnlyList<WorkspaceLaunchItem> LaunchItems => _launchItems;
    public void SetLaunchItems(IEnumerable<WorkspaceLaunchItem> items) => _launchItems = [.. items];
    public AutomationSettings ToAutomation() => new()
    {
        IdleMinutes = IdleMinutes,
        MouseTrigger = EnableLowLevelMouseHook ? MouseTrigger : MouseAutomationTrigger.None,
        EnableLowLevelMouseHook = EnableLowLevelMouseHook,
        CooldownMilliseconds = CooldownMilliseconds
    };
    public SceneProfile ToModel() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名场景" : Name.Trim(),
        Hotkey = Hotkey,
        Mode = Mode,
        PrivacyShellMode = PrivacyShellMode,
        Targets = [.. _targets],
        LaunchItems = [.. _launchItems],
        Automation = ToAutomation()
    };
}
