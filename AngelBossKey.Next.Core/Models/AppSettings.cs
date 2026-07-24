namespace AngelBossKey.Next.Core.Models;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public HotkeyGesture Hotkey { get; init; } = new();
    public List<TargetRule> Targets { get; init; } = [];
    public bool LaunchAtLogin { get; init; }
    public bool CloseToTray { get; init; } = true;
}
