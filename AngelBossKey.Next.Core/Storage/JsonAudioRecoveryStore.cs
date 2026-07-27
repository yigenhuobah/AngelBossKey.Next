using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Core.Storage;

public sealed class JsonAudioRecoveryStore(string path) : IAudioRecoveryStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<AudioRecoveryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = await AtomicJsonStore.ReadAsync<AudioRecoveryState>(path, cancellationToken);
        if (state is null) return new AudioRecoveryState();
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported audio recovery state schema.");
        }
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
