namespace AngelBossKey.Next.Core.Models;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 7;
    // Kept for lossless migration from v0.1/v0.2 settings.
    public HotkeyGesture Hotkey { get; init; } = new();
    public List<TargetRule> Targets { get; init; } = [];
    public List<SceneProfile> Scenes { get; init; } = [];
    public Guid ActiveSceneId { get; init; }
    public bool LaunchAtLogin { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool EnableElevatedBroker { get; init; }
}

public enum SceneMode
{
    HideWindows,
    PrivacyDesktop
}

public enum PrivacyDesktopShellMode
{
    FullExplorer,
    Compatibility
}

public enum MouseAutomationTrigger
{
    None,
    XButton1,
    XButton2,
    MiddleButton,
    WheelUp,
    WheelDown
}

public enum AutomationTriggerSource
{
    Idle,
    Mouse
}

public sealed class AutomationTriggeredEventArgs(AutomationTriggerSource source) : EventArgs
{
    public AutomationTriggerSource Source { get; } = source;
}

public sealed record AutomationSettings
{
    public int IdleMinutes { get; init; }
    public MouseAutomationTrigger MouseTrigger { get; init; }
    public bool EnableLowLevelMouseHook { get; init; }
    public int CooldownMilliseconds { get; init; } = 1000;
}

public sealed record SceneProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "默认场景";
    public HotkeyGesture Hotkey { get; init; } = new();
    public List<TargetRule> Targets { get; init; } = [];
    public AutomationSettings Automation { get; init; } = new();
    public SceneMode Mode { get; init; }
    public PrivacyDesktopShellMode PrivacyShellMode { get; init; } = PrivacyDesktopShellMode.FullExplorer;
}
