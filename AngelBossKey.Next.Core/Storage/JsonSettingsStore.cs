using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using System.Text.Json;

namespace AngelBossKey.Next.Core.Storage;

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await AtomicJsonStore.ReadAsync<AppSettings>(path, cancellationToken);
            if (settings is not { SchemaVersion: 1 })
            {
                return new AppSettings();
            }

            return settings with
            {
                Hotkey = settings.Hotkey ?? new HotkeyGesture(),
                Targets = (settings.Targets ?? [])
                    .Where(target => target is not null &&
                        !string.IsNullOrWhiteSpace(target.DisplayName) &&
                        !string.IsNullOrWhiteSpace(target.ExecutablePath))
                    .ToList()
            };
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await AtomicJsonStore.WriteAsync(path, settings, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
