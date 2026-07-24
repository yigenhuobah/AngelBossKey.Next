namespace AngelBossKey.Next.Core.Models;

public sealed record WindowInfo
{
    public required long Handle { get; init; }
    public required int ProcessId { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public required string DisplayName { get; init; }
    public required string ExecutablePath { get; init; }
}
