namespace AngelBossKey.Next.Core.Models;

public sealed record TargetRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ExecutablePath { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public string TitleIncludes { get; init; } = string.Empty;
    public string TitleExcludes { get; init; } = string.Empty;
    public bool MuteWhenHidden { get; init; }
}
