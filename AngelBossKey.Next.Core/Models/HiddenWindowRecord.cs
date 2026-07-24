namespace AngelBossKey.Next.Core.Models;

public sealed record WindowPlacementSnapshot
{
    public int Flags { get; init; }
    public int ShowCommand { get; init; }
    public int MinPositionX { get; init; }
    public int MinPositionY { get; init; }
    public int MaxPositionX { get; init; }
    public int MaxPositionY { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
}

public sealed record HiddenWindowRecord
{
    public required long Handle { get; init; }
    public required int ProcessId { get; init; }
    public required long ProcessStartTimeUtcTicks { get; init; }
    public required string ExecutablePath { get; init; }
    public required WindowPlacementSnapshot Placement { get; init; }
    public bool WasForeground { get; init; }
    public bool RequiresElevatedBroker { get; init; }
    public DateTimeOffset HiddenAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RecoveryState
{
    public int SchemaVersion { get; init; } = 1;
    public List<HiddenWindowRecord> Windows { get; init; } = [];
}

public sealed record VisibilityOperationResult
{
    public int ChangedCount { get; init; }
    public int SkippedElevatedCount { get; init; }
    public int FailedCount { get; init; }
    public string? Detail { get; init; }
}

public enum ElevatedWindowCommand
{
    Query,
    Hide,
    Restore
}

public sealed record ElevatedWindowRequest
{
    public ElevatedWindowCommand Command { get; init; }
    public List<long> Handles { get; init; } = [];
    public List<HiddenWindowRecord> Windows { get; init; } = [];
}

public sealed record ElevatedWindowResponse
{
    public int ChangedCount { get; init; }
    public int FailedCount { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record AudioSessionSnapshot
{
    public required string SessionId { get; init; }
    public required int ProcessId { get; init; }
    public required long ProcessStartTimeUtcTicks { get; init; }
    public required string ExecutablePath { get; init; }
    public float Volume { get; init; }
    public bool Muted { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AudioRecoveryState
{
    public int SchemaVersion { get; init; } = 1;
    public List<AudioSessionSnapshot> Sessions { get; init; } = [];
}
