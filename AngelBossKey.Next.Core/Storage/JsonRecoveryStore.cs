using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using System.Text.Json;

namespace AngelBossKey.Next.Core.Storage;

public sealed class JsonRecoveryStore(string path) : IRecoveryStore
{
    public async Task<RecoveryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await AtomicJsonStore.ReadAsync<RecoveryState>(path, cancellationToken);
            if (state is not { SchemaVersion: 1 })
            {
                return new RecoveryState();
            }

            return state with
            {
                Windows = (state.Windows ?? [])
                    .Where(record => record is not null &&
                        record.Handle != 0 &&
                        record.ProcessId > 0 &&
                        record.ProcessStartTimeUtcTicks > 0 &&
                        !string.IsNullOrWhiteSpace(record.ExecutablePath) &&
                        record.Placement is not null)
                    .ToList()
            };
        }
        catch (JsonException)
        {
            return new RecoveryState();
        }
        catch (IOException)
        {
            return new RecoveryState();
        }
        catch (UnauthorizedAccessException)
        {
            return new RecoveryState();
        }
    }

    public Task SaveAsync(RecoveryState state, CancellationToken cancellationToken = default) =>
        AtomicJsonStore.WriteAsync(path, state, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var temporaryPath = path + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        return Task.CompletedTask;
    }
}
