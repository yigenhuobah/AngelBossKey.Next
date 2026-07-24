using NAudio.CoreAudioApi;

namespace AngelBossKey.Next.Win32;

public sealed record ProcessAudioSession
{
    public required string SessionId { get; init; }
    public required int ProcessId { get; init; }
    public required string ExecutablePath { get; init; }
    public float Volume { get; init; }
    public bool Muted { get; init; }
}

public sealed record AudioSessionUpdate
{
    public required string SessionId { get; init; }
    public float? Volume { get; init; }
    public bool Muted { get; init; }
}

public interface IAudioSessionBackend
{
    IReadOnlyList<ProcessAudioSession> Enumerate();
    IReadOnlySet<string> Apply(IReadOnlyCollection<AudioSessionUpdate> updates);
}

public sealed class NAudioSessionBackend : IAudioSessionBackend
{
    public IReadOnlyList<ProcessAudioSession> Enumerate()
    {
        var result = new List<ProcessAudioSession>();
        VisitSessions((session, sessionId, processId, path) =>
        {
            var volume = session.SimpleAudioVolume;
            result.Add(new ProcessAudioSession
            {
                SessionId = sessionId,
                ProcessId = processId,
                ExecutablePath = path,
                Volume = volume.Volume,
                Muted = volume.Mute
            });
        });
        return result;
    }

    public IReadOnlySet<string> Apply(IReadOnlyCollection<AudioSessionUpdate> updates)
    {
        var byId = updates.ToDictionary(update => update.SessionId, StringComparer.Ordinal);
        var failed = byId.Keys.ToHashSet(StringComparer.Ordinal);
        VisitSessions((session, sessionId, _, _) =>
        {
            if (!byId.TryGetValue(sessionId, out var update)) return;
            try
            {
                var volume = session.SimpleAudioVolume;
                if (update.Volume is { } level) volume.Volume = level;
                volume.Mute = update.Muted;
                failed.Remove(sessionId);
            }
            catch
            {
                // Keep the update pending for a later retry.
            }
        });
        return failed;
    }

    private static void VisitSessions(Action<AudioSessionControl, string, int, string> visitor)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            using var device = devices[deviceIndex];
            var sessions = device.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                int processId;
                string path;
                string sessionId;
                try
                {
                    processId = (int)session.GetProcessID;
                    if (processId <= 0) continue;
                    path = ProcessPathResolver.TryGetPath(processId);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    sessionId = $"{device.ID}|{session.GetSessionInstanceIdentifier}";
                }
                catch
                {
                    continue;
                }

                visitor(session, sessionId, processId, path);
            }
        }
    }
}
