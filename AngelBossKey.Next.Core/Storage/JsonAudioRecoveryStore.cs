using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using System.Text.Json;

namespace AngelBossKey.Next.Core.Storage;

public sealed class JsonAudioRecoveryStore(string path) : IAudioRecoveryStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<AudioRecoveryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await AtomicJsonStore.ReadAsync<AudioRecoveryState>(path, cancellationToken);
            if (state is not { SchemaVersion: 1 }) return new AudioRecoveryState();
            return state with
            {
                Sessions = (state.Sessions ?? [])
                    .Where(session => session is not null &&
                        !string.IsNullOrWhiteSpace(session.SessionId) &&
                        session.ProcessId > 0 &&
                        session.ProcessStartTimeUtcTicks > 0 &&
                        !string.IsNullOrWhiteSpace(session.ExecutablePath) &&
                        session.Volume is >= 0 and <= 1)
                    .ToList()
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AudioRecoveryState();
        }
    }

    public async Task SaveAsync(AudioRecoveryState state, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try { await AtomicJsonStore.WriteAsync(path, state, cancellationToken); }
        finally { _writeGate.Release(); }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        return Task.CompletedTask;
    }
}
