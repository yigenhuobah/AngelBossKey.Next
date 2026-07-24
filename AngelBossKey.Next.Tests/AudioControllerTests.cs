using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Win32;

namespace AngelBossKey.Next.Tests;

public sealed class AudioControllerTests
{
    [Fact]
    public async Task Controller_JournalsBeforeMutingAndRestoresEverySessionExactly()
    {
        var store = new MemoryAudioRecoveryStore();
        var backend = new FakeAudioSessionBackend(store);
        backend.Sessions.Add(CreateSession("first", 0.35f, false));
        using var controller = new ApplicationAudioController(backend, store);
        var target = new TargetRule
        {
            DisplayName = "Test",
            ExecutablePath = Environment.ProcessPath!,
            MuteWhenHidden = true
        };

        var muted = await controller.MuteAsync([target]);
        backend.Sessions.Add(CreateSession("second", 0.7f, false));
        await controller.ReconcileAsync([target]);

        Assert.Equal(1, muted);
        Assert.True(backend.JournalWasPresentBeforeFirstMutation);
        Assert.All(backend.Sessions, session => Assert.True(session.Muted));
        Assert.Equal(2, store.State.Sessions.Count);

        var restored = await controller.RestoreAsync();

        Assert.Equal(2, restored);
        Assert.Equal(0.35f, backend.Sessions[0].Volume);
        Assert.Equal(0.7f, backend.Sessions[1].Volume);
        Assert.All(backend.Sessions, session => Assert.False(session.Muted));
        Assert.Empty(store.State.Sessions);
    }

    [Fact]
    public async Task Controller_KeepsRecoveryEntryWhenLiveSessionRestoreFails()
    {
        var store = new MemoryAudioRecoveryStore();
        var backend = new FakeAudioSessionBackend(store);
        backend.Sessions.Add(CreateSession("pending", 0.5f, false));
        using var controller = new ApplicationAudioController(backend, store);
        var target = new TargetRule
        {
            DisplayName = "Test",
            ExecutablePath = Environment.ProcessPath!,
            MuteWhenHidden = true
        };
        await controller.MuteAsync([target]);
        backend.FailedUpdates.Add("pending");

        var restored = await controller.RestoreAsync();

        Assert.Equal(0, restored);
        Assert.Single(store.State.Sessions);
        Assert.True(backend.Sessions[0].Muted);
    }

    [Fact]
    public async Task Controller_RetriesPendingRestoreWhenSessionReturns()
    {
        var store = new MemoryAudioRecoveryStore();
        var backend = new FakeAudioSessionBackend(store);
        var session = CreateSession("temporary", 0.45f, false);
        backend.Sessions.Add(session);
        using var controller = new ApplicationAudioController(
            backend,
            store,
            monitorInterval: TimeSpan.FromMilliseconds(10));
        var target = new TargetRule
        {
            DisplayName = "Test",
            ExecutablePath = Environment.ProcessPath!,
            MuteWhenHidden = true
        };
        await controller.MuteAsync([target]);
        backend.Sessions.Clear();

        Assert.Equal(0, await controller.RestoreAsync());
        Assert.Equal(1, controller.PendingRestoreCount);

        backend.Sessions.Add(session with { Muted = true });
        Assert.True(SpinWait.SpinUntil(
            () => controller.PendingRestoreCount == 0,
            TimeSpan.FromSeconds(2)));
        Assert.False(backend.Sessions.Single().Muted);
        Assert.Equal(0.45f, backend.Sessions.Single().Volume);
        Assert.Empty(store.State.Sessions);
    }

    private static ProcessAudioSession CreateSession(string id, float volume, bool muted) => new()
    {
        SessionId = id,
        ProcessId = Environment.ProcessId,
        ExecutablePath = Environment.ProcessPath!,
        Volume = volume,
        Muted = muted
    };

    private sealed class MemoryAudioRecoveryStore : IAudioRecoveryStore
    {
        public AudioRecoveryState State { get; private set; } = new();
        public Task<AudioRecoveryState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);
        public Task SaveAsync(AudioRecoveryState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            State = new AudioRecoveryState();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudioSessionBackend(MemoryAudioRecoveryStore store) : IAudioSessionBackend
    {
        public List<ProcessAudioSession> Sessions { get; } = [];
        public HashSet<string> FailedUpdates { get; } = new(StringComparer.Ordinal);
        public bool JournalWasPresentBeforeFirstMutation { get; private set; }

        public IReadOnlyList<ProcessAudioSession> Enumerate() => [.. Sessions];

        public IReadOnlySet<string> Apply(IReadOnlyCollection<AudioSessionUpdate> updates)
        {
            var failed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var update in updates)
            {
                if (FailedUpdates.Contains(update.SessionId))
                {
                    failed.Add(update.SessionId);
                    continue;
                }

                var index = Sessions.FindIndex(session => session.SessionId == update.SessionId);
                if (index < 0)
                {
                    failed.Add(update.SessionId);
                    continue;
                }

                JournalWasPresentBeforeFirstMutation |= store.State.Sessions.Any(
                    session => session.SessionId == update.SessionId);
                Sessions[index] = Sessions[index] with
                {
                    Volume = update.Volume ?? Sessions[index].Volume,
                    Muted = update.Muted
                };
            }
            return failed;
        }
    }
}
