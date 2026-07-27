using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Win32;

namespace AngelBossKey.Next.Tests;

public sealed class AudioRecoveryReliabilityTests
{
    private static readonly string[] ExecutablePaths =
    [
        @"C:\Reliability\alpha.exe",
        @"C:\Reliability\beta.exe",
        @"C:\Reliability\gamma.exe"
    ];

    [Fact]
    [Trait("Category", "Reliability")]
    public async Task Controller_AtomicallyReplacesStaleSnapshotBeforeMutingReusedSessionIdentity()
    {
        var processes = new SimulatedProcessTable();
        var store = new FaultingAudioRecoveryStore();
        var backend = new ModelAudioBackend(store, processes);
        const int processId = 4100;
        const string sessionId = "session-0";
        var firstStartTime = processes.Start(processId);
        backend.Set(new ProcessAudioSession
        {
            SessionId = sessionId,
            ProcessId = processId,
            ExecutablePath = ExecutablePaths[0],
            Volume = 0.25f,
            Muted = false
        });
        using var controller = CreateController(backend, store, processes);

        await controller.MuteAsync(CreateTargets([ExecutablePaths[0]]));
        Assert.True(backend.Get(sessionId)!.Muted);
        Assert.Equal(firstStartTime, Assert.Single(store.State.Sessions).ProcessStartTimeUtcTicks);

        processes.Stop(processId);
        var secondStartTime = processes.Start(processId);
        backend.Set(new ProcessAudioSession
        {
            SessionId = sessionId,
            ProcessId = processId,
            ExecutablePath = ExecutablePaths[0],
            Volume = 0.8f,
            Muted = false
        });

        store.FailNextWrite();
        await controller.ReconcileAsync(CreateTargets([ExecutablePaths[0]]));

        Assert.False(backend.Get(sessionId)!.Muted);
        Assert.Equal(firstStartTime, Assert.Single(store.State.Sessions).ProcessStartTimeUtcTicks);

        await controller.ReconcileAsync(CreateTargets([ExecutablePaths[0]]));

        var current = backend.Get(sessionId)!;
        var snapshot = Assert.Single(store.State.Sessions);
        Assert.True(current.Muted);
        Assert.Equal(secondStartTime, snapshot.ProcessStartTimeUtcTicks);
        Assert.Equal(0.8f, snapshot.Volume);
        Assert.False(snapshot.Muted);
        Assert.Null(backend.SafetyViolation);
    }

    [Fact]
    [Trait("Category", "Reliability")]
    public async Task Backend_RejectsSessionReplacedBetweenSnapshotAndAudioMutation()
    {
        var processes = new SimulatedProcessTable();
        var store = new FaultingAudioRecoveryStore();
        var backend = new ModelAudioBackend(store, processes);
        const int processId = 4200;
        const string sessionId = "session-race";
        var firstStartTime = processes.Start(processId);
        backend.Set(new ProcessAudioSession
        {
            SessionId = sessionId,
            ProcessId = processId,
            ExecutablePath = ExecutablePaths[0],
            Volume = 0.25f,
            Muted = false
        });
        using var controller = CreateController(backend, store, processes);

        long secondStartTime = 0;
        backend.BeforeNextApply(() =>
        {
            processes.Stop(processId);
            secondStartTime = processes.Start(processId);
            backend.Set(new ProcessAudioSession
            {
                SessionId = sessionId,
                ProcessId = processId,
                ExecutablePath = ExecutablePaths[0],
                Volume = 0.8f,
                Muted = false
            });
        });

        await controller.MuteAsync(CreateTargets([ExecutablePaths[0]]));

        Assert.False(backend.Get(sessionId)!.Muted);
        Assert.Equal(firstStartTime, Assert.Single(store.State.Sessions).ProcessStartTimeUtcTicks);
        Assert.Null(backend.SafetyViolation);

        await controller.ReconcileAsync(CreateTargets([ExecutablePaths[0]]));

        var snapshot = Assert.Single(store.State.Sessions);
        Assert.True(backend.Get(sessionId)!.Muted);
        Assert.Equal(secondStartTime, snapshot.ProcessStartTimeUtcTicks);
        Assert.Equal(0.8f, snapshot.Volume);
        Assert.Null(backend.SafetyViolation);
    }

    [Fact]
    [Trait("Category", "Reliability")]
    public async Task Controller_RandomizedFailuresPreserveDurableRecoveryInvariants()
    {
        var configuredSteps = ReliabilityTestSettings.StepCount;
        for (var index = 0; index < ReliabilityTestSettings.SeedCount; index++)
        {
            var seed = unchecked(ReliabilityTestSettings.BaseSeed + (index * 7_919));
            using var scenario = new AudioRecoveryScenario(seed);
            try
            {
                await scenario.RunAsync(configuredSteps);
            }
            catch (Exception exception)
            {
                ReliabilityTestSettings.WriteFailureTrace(
                    seed,
                    configuredSteps,
                    scenario.Operations,
                    exception);
                throw new InvalidOperationException(
                    $"Audio recovery model failed for seed {seed}. " +
                    $"Reproduce with -BaseSeed {seed} -Seeds 1 -Steps {configuredSteps}.",
                    exception);
            }
        }
    }

    private static ApplicationAudioController CreateController(
        IAudioSessionBackend backend,
        IAudioRecoveryStore store,
        SimulatedProcessTable processes) =>
        new(
            backend,
            store,
            diagnosticLog: null,
            monitorInterval: TimeSpan.FromHours(12),
            getProcessStartTimeUtcTicks: processes.GetStartTime);

    private static TargetRule[] CreateTargets(IEnumerable<string> paths) =>
        paths.Select(path => new TargetRule
        {
            DisplayName = Path.GetFileNameWithoutExtension(path),
            ExecutablePath = path,
            MuteWhenHidden = true
        }).ToArray();

    private sealed class AudioRecoveryScenario : IDisposable
    {
        private const int SessionSlots = 8;
        private readonly Random _random;
        private readonly SimulatedProcessTable _processes = new();
        private readonly FaultingAudioRecoveryStore _store = new();
        private readonly ModelAudioBackend _backend;
        private readonly Dictionary<string, ExpectedSession> _expected = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProcessAudioSession> _detached = new(StringComparer.Ordinal);
        private ApplicationAudioController _controller;
        private string[] _activePaths = [];
        private bool _hidden;

        internal AudioRecoveryScenario(int seed)
        {
            _random = new Random(seed);
            _backend = new ModelAudioBackend(_store, _processes);
            _controller = CreateController(_backend, _store, _processes);
            Operations.Add($"seed={seed}");
        }

        internal List<string> Operations { get; } = [];

        internal async Task RunAsync(int stepCount)
        {
            for (var slot = 0; slot < 3; slot++) StartOrReturnSession(slot);

            for (var step = 0; step < stepCount; step++)
            {
                switch (_random.Next(11))
                {
                    case 0:
                        StartOrReturnSession(_random.Next(SessionSlots));
                        break;
                    case 1:
                        DetachSession(_random.Next(SessionSlots));
                        break;
                    case 2:
                        ExitProcess(_random.Next(SessionSlots));
                        break;
                    case 3:
                        ReuseProcessIdentity(_random.Next(SessionSlots));
                        break;
                    case 4:
                        await EnterOrChangeTargetsAsync();
                        break;
                    case 5:
                        await ReconcileAsync();
                        break;
                    case 6:
                        await RestoreAsync();
                        break;
                    case 7:
                        await RestartAndRecoverAsync();
                        break;
                    case 8:
                        _store.FailNextWrite();
                        AddOperation("fail-next-journal-write");
                        await ReconcileAsync();
                        break;
                    case 9:
                        _backend.FailNextApply();
                        AddOperation("fail-next-audio-update");
                        await ReconcileAsync();
                        break;
                    default:
                        await HealthyCheckpointAsync();
                        break;
                }

                AssertDurableRecoveryInvariant(step);
            }

            await FinishCleanlyAsync();
        }

        public void Dispose() => _controller.Dispose();

        private void StartOrReturnSession(int slot)
        {
            var sessionId = GetSessionId(slot);
            if (_backend.Get(sessionId) is not null) return;
            if (_detached.Remove(sessionId, out var detached) && _processes.GetStartTime(detached.ProcessId) > 0)
            {
                _backend.Set(detached);
                AddOperation($"return {sessionId}");
                return;
            }

            StartNewProcessSession(slot, "start");
        }

        private void DetachSession(int slot)
        {
            var sessionId = GetSessionId(slot);
            var session = _backend.Remove(sessionId);
            if (session is null) return;
            _detached[sessionId] = session;
            AddOperation($"detach {sessionId}");
        }

        private void ExitProcess(int slot)
        {
            var processId = GetProcessId(slot);
            var sessionId = GetSessionId(slot);
            _backend.Remove(sessionId);
            _detached.Remove(sessionId);
            _processes.Stop(processId);
            AddOperation($"exit pid={processId}");
        }

        private void ReuseProcessIdentity(int slot)
        {
            ExitProcess(slot);
            StartNewProcessSession(slot, "reuse");
        }

        private void StartNewProcessSession(int slot, string operation)
        {
            var processId = GetProcessId(slot);
            var sessionId = GetSessionId(slot);
            var startTime = _processes.Start(processId);
            var session = new ProcessAudioSession
            {
                SessionId = sessionId,
                ProcessId = processId,
                ExecutablePath = ExecutablePaths[_random.Next(ExecutablePaths.Length)],
                Volume = (float)(_random.Next(10, 91) / 100d),
                Muted = _random.Next(5) == 0
            };
            _expected[sessionId] = new ExpectedSession(session, startTime);
            _backend.Set(session);
            AddOperation($"{operation} {sessionId} epoch={startTime} path={Path.GetFileName(session.ExecutablePath)}");
        }

        private async Task EnterOrChangeTargetsAsync()
        {
            _activePaths = ExecutablePaths.Where(_ => _random.Next(2) == 0).ToArray();
            var targets = CreateTargets(_activePaths);
            if (_hidden)
            {
                await _controller.ReconcileAsync(targets);
            }
            else
            {
                await _controller.MuteAsync(targets);
            }

            _hidden = _activePaths.Length > 0;
            AddOperation($"targets [{string.Join(',', _activePaths.Select(Path.GetFileName))}]");
        }

        private async Task ReconcileAsync()
        {
            if (!_hidden) return;
            await _controller.ReconcileAsync(CreateTargets(_activePaths));
            AddOperation("reconcile");
        }

        private async Task RestoreAsync()
        {
            try
            {
                await _controller.RestoreAsync();
            }
            catch (InjectedJournalWriteException)
            {
                AddOperation("restore -> injected-journal-failure");
            }
            _hidden = false;
            _activePaths = [];
            AddOperation("restore");
        }

        private async Task RestartAndRecoverAsync()
        {
            _controller.Dispose();
            _controller = CreateController(_backend, _store, _processes);
            try
            {
                await _controller.RecoverAsync();
            }
            catch (InjectedJournalWriteException)
            {
                AddOperation("recover -> injected-journal-failure");
            }
            _hidden = false;
            _activePaths = [];
            AddOperation("restart-and-recover");
        }

        private async Task HealthyCheckpointAsync()
        {
            _store.ClearFailures();
            _backend.ClearFailures();
            if (_hidden)
            {
                await _controller.ReconcileAsync(CreateTargets(_activePaths));
                AssertAllTargetedSessionsMuted();
                AddOperation("healthy-reconcile");
            }
            else
            {
                await _controller.RestoreAsync();
                AssertAllPresentSessionsRestored();
                AddOperation("healthy-restore");
            }
        }

        private async Task FinishCleanlyAsync()
        {
            _store.ClearFailures();
            _backend.ClearFailures();
            foreach (var slot in Enumerable.Range(0, SessionSlots))
            {
                ExitProcess(slot);
            }
            await _controller.RestoreAsync();
            Assert.Empty(_store.State.Sessions);
            Assert.Null(_backend.SafetyViolation);
            AddOperation("finish-clean");
        }

        private void AssertDurableRecoveryInvariant(int step)
        {
            Assert.Null(_backend.SafetyViolation);
            foreach (var session in _backend.Sessions)
            {
                var expected = _expected[session.SessionId];
                if (HasOriginalState(session, expected.Initial)) continue;

                var snapshot = _store.State.Sessions.SingleOrDefault(candidate =>
                    candidate.SessionId == session.SessionId &&
                    candidate.ProcessId == session.ProcessId &&
                    candidate.ProcessStartTimeUtcTicks == expected.ProcessStartTimeUtcTicks &&
                    string.Equals(candidate.ExecutablePath, session.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                Assert.True(
                    snapshot is not null &&
                    snapshot.Volume == expected.Initial.Volume &&
                    snapshot.Muted == expected.Initial.Muted,
                    $"Step {step}: mutated session {session.SessionId} has no matching durable snapshot.");
            }
        }

        private void AssertAllTargetedSessionsMuted()
        {
            foreach (var session in _backend.Sessions.Where(session =>
                _activePaths.Contains(session.ExecutablePath, StringComparer.OrdinalIgnoreCase)))
            {
                Assert.True(session.Muted, $"Targeted session {session.SessionId} was not muted.");
            }
        }

        private void AssertAllPresentSessionsRestored()
        {
            foreach (var session in _backend.Sessions)
            {
                Assert.True(
                    HasOriginalState(session, _expected[session.SessionId].Initial),
                    $"Session {session.SessionId} did not return to its original state.");
            }
        }

        private void AddOperation(string operation)
        {
            Operations.Add(operation);
            if (Operations.Count > 256) Operations.RemoveAt(1);
        }

        private static bool HasOriginalState(ProcessAudioSession actual, ProcessAudioSession expected) =>
            actual.ProcessId == expected.ProcessId &&
            string.Equals(actual.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            actual.Volume == expected.Volume &&
            actual.Muted == expected.Muted;

        private static int GetProcessId(int slot) => 4_100 + slot;
        private static string GetSessionId(int slot) => $"session-{slot}";

        private sealed record ExpectedSession(ProcessAudioSession Initial, long ProcessStartTimeUtcTicks);
    }

    private sealed class SimulatedProcessTable
    {
        private readonly Dictionary<int, long> _startTimes = [];
        private long _nextStartTime = 10_000;

        internal long Start(int processId)
        {
            var startTime = ++_nextStartTime;
            _startTimes[processId] = startTime;
            return startTime;
        }

        internal void Stop(int processId) => _startTimes.Remove(processId);

        internal long GetStartTime(int processId) =>
            _startTimes.TryGetValue(processId, out var startTime) ? startTime : 0;
    }

    private sealed class FaultingAudioRecoveryStore : IAudioRecoveryStore
    {
        private int _writeFailuresRemaining;

        internal AudioRecoveryState State { get; private set; } = new();

        public Task<AudioRecoveryState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Clone(State));

        public Task SaveAsync(AudioRecoveryState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfWriteShouldFail();
            State = Clone(state);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfWriteShouldFail();
            State = new AudioRecoveryState();
            return Task.CompletedTask;
        }

        internal void FailNextWrite() => _writeFailuresRemaining++;
        internal void ClearFailures() => _writeFailuresRemaining = 0;

        private void ThrowIfWriteShouldFail()
        {
            if (_writeFailuresRemaining <= 0) return;
            _writeFailuresRemaining--;
            throw new InjectedJournalWriteException();
        }

        private static AudioRecoveryState Clone(AudioRecoveryState state) =>
            state with { Sessions = [.. state.Sessions] };
    }

    private sealed class InjectedJournalWriteException()
        : IOException("Injected audio recovery journal failure.");

    private sealed class ModelAudioBackend(
        FaultingAudioRecoveryStore store,
        SimulatedProcessTable processes) : IAudioSessionBackend
    {
        private readonly Dictionary<string, ProcessAudioSession> _sessions = new(StringComparer.Ordinal);
        private int _applyFailuresRemaining;
        private Action? _beforeNextApply;

        internal IReadOnlyCollection<ProcessAudioSession> Sessions => _sessions.Values;
        internal string? SafetyViolation { get; private set; }

        public IReadOnlyList<ProcessAudioSession> Enumerate() => [.. _sessions.Values];

        public IReadOnlySet<string> Apply(IReadOnlyCollection<AudioSessionUpdate> updates)
        {
            var beforeApply = _beforeNextApply;
            _beforeNextApply = null;
            beforeApply?.Invoke();

            if (_applyFailuresRemaining > 0)
            {
                _applyFailuresRemaining--;
                return updates.Select(update => update.SessionId).ToHashSet(StringComparer.Ordinal);
            }

            var failed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var update in updates)
            {
                if (!_sessions.TryGetValue(update.SessionId, out var current))
                {
                    failed.Add(update.SessionId);
                    continue;
                }
                var startTime = processes.GetStartTime(current.ProcessId);
                if (update.ProcessId != current.ProcessId ||
                    update.ProcessStartTimeUtcTicks <= 0 ||
                    update.ProcessStartTimeUtcTicks != startTime ||
                    !string.Equals(update.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(update.SessionId);
                    continue;
                }
                if (update.Muted && !current.Muted)
                {
                    var snapshot = store.State.Sessions.SingleOrDefault(candidate =>
                        candidate.SessionId == current.SessionId &&
                        candidate.ProcessId == current.ProcessId &&
                        candidate.ProcessStartTimeUtcTicks == startTime &&
                        string.Equals(candidate.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        candidate.Volume == current.Volume &&
                        candidate.Muted == current.Muted);
                    if (snapshot is null)
                    {
                        SafetyViolation = $"Mute requested for {current.SessionId} before its snapshot was durable.";
                    }
                }
                _sessions[update.SessionId] = current with
                {
                    Volume = update.Volume ?? current.Volume,
                    Muted = update.Muted
                };
            }
            return failed;
        }

        internal ProcessAudioSession? Get(string sessionId) =>
            _sessions.GetValueOrDefault(sessionId);

        internal void Set(ProcessAudioSession session) =>
            _sessions[session.SessionId] = session;

        internal ProcessAudioSession? Remove(string sessionId) =>
            _sessions.Remove(sessionId, out var session) ? session : null;

        internal void FailNextApply() => _applyFailuresRemaining++;
        internal void BeforeNextApply(Action action) => _beforeNextApply = action;
        internal void ClearFailures() => _applyFailuresRemaining = 0;
    }
}
