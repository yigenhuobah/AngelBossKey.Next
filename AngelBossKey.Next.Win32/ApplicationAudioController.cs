using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;

namespace AngelBossKey.Next.Win32;

public sealed class ApplicationAudioController : IApplicationAudioController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AudioSessionSnapshot> _savedSessions = new(StringComparer.Ordinal);
    private readonly IDiagnosticLog _log;
    private readonly IAudioRecoveryStore _recoveryStore;
    private readonly IAudioSessionBackend _backend;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private TargetRule[] _activeTargets = [];
    private Task? _monitorTask;
    private bool _disposed;
    private bool _isActive;
    private int _pendingRestoreCount;

    public ApplicationAudioController(IDiagnosticLog? diagnosticLog = null)
        : this(new NAudioSessionBackend(), new NullAudioRecoveryStore(), diagnosticLog)
    {
    }

    public ApplicationAudioController(
        IAudioRecoveryStore recoveryStore,
        IDiagnosticLog? diagnosticLog = null)
        : this(new NAudioSessionBackend(), recoveryStore, diagnosticLog)
    {
    }

    public ApplicationAudioController(
        IAudioSessionBackend backend,
        IAudioRecoveryStore recoveryStore,
        IDiagnosticLog? diagnosticLog = null,
        TimeSpan? monitorInterval = null)
    {
        _backend = backend;
        _recoveryStore = recoveryStore;
        _log = diagnosticLog ?? NullDiagnosticLog.Instance;
        _timer = new PeriodicTimer(monitorInterval ?? TimeSpan.FromSeconds(1));
    }

    public bool IsActive => Volatile.Read(ref _isActive);
    public int PendingRestoreCount
    {
        get
        {
            if (!_gate.Wait(0)) return Volatile.Read(ref _pendingRestoreCount);
            try
            {
                var count = _isActive ? 0 : _savedSessions.Count;
                Volatile.Write(ref _pendingRestoreCount, count);
                return count;
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<int> MuteAsync(
        IReadOnlyCollection<TargetRule> targets,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _activeTargets = targets.Where(target => target.Enabled && target.MuteWhenHidden).ToArray();
            var changed = await CaptureAndMuteAsync(cancellationToken);
            Volatile.Write(ref _isActive, _activeTargets.Length > 0);
            Volatile.Write(ref _pendingRestoreCount, _isActive ? 0 : _savedSessions.Count);
            EnsureMonitorStarted();

            _log.Info("audio.mute", $"sessions={changed}; targets={_activeTargets.Length}");
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReconcileAsync(
        IReadOnlyCollection<TargetRule> targets,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _activeTargets = targets.Where(target => target.Enabled && target.MuteWhenHidden).ToArray();
            try
            {
                var removedIds = _savedSessions
                    .Where(pair => !MatchesPath(pair.Value.ExecutablePath, _activeTargets))
                    .Select(pair => pair.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var attempt = RestoreSessions(removedIds);
                foreach (var id in removedIds.Except(attempt.FailedSessionIds)) _savedSessions.Remove(id);
                await SaveOrClearJournalAsync(cancellationToken);
                await CaptureAndMuteAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _log.LogError("audio.reconcile", exception);
            }
            Volatile.Write(ref _isActive, _activeTargets.Length > 0);
            Volatile.Write(ref _pendingRestoreCount, _isActive ? 0 : _savedSessions.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _activeTargets = [];
            Volatile.Write(ref _isActive, false);
            try
            {
                var attempt = RestoreSessions(_savedSessions.Keys.ToHashSet(StringComparer.Ordinal));
                RetainOnly(attempt.FailedSessionIds);
                await SaveOrClearJournalAsync(cancellationToken);
                Volatile.Write(ref _pendingRestoreCount, _savedSessions.Count);
                EnsureMonitorStarted();
                _log.Info("audio.restore", $"sessions={attempt.RestoredCount}; pending={_savedSessions.Count}");
                return attempt.RestoredCount;
            }
            catch (Exception exception)
            {
                _log.LogError("audio.restore", exception);
                await SaveOrClearJournalAsync(cancellationToken);
                EnsureMonitorStarted();
                return 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await _recoveryStore.LoadAsync(cancellationToken);
            _savedSessions.Clear();
            foreach (var session in state.Sessions)
            {
                _savedSessions[session.SessionId] = session;
            }
            Volatile.Write(ref _pendingRestoreCount, _savedSessions.Count);
            try
            {
                var attempt = RestoreSessions(_savedSessions.Keys.ToHashSet(StringComparer.Ordinal));
                RetainOnly(attempt.FailedSessionIds);
                await SaveOrClearJournalAsync(cancellationToken);
                Volatile.Write(ref _pendingRestoreCount, _savedSessions.Count);
                EnsureMonitorStarted();
                if (attempt.RestoredCount > 0) _log.Info("audio.recover", $"sessions={attempt.RestoredCount}");
                return attempt.RestoredCount;
            }
            catch (Exception exception)
            {
                _log.LogError("audio.recover", exception);
                await SaveOrClearJournalAsync(cancellationToken);
                EnsureMonitorStarted();
                return 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _timer.Dispose();
        try { _monitorTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        try { RestoreAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { _log.LogError("audio.dispose", exception); }
        _shutdown.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (_activeTargets.Length > 0)
                    {
                        await CaptureAndMuteAsync(cancellationToken);
                    }
                    else if (_savedSessions.Count > 0)
                    {
                        var before = _savedSessions.Count;
                        var attempt = RestoreSessions(_savedSessions.Keys.ToHashSet(StringComparer.Ordinal));
                        RetainOnly(attempt.FailedSessionIds);
                        if (_savedSessions.Count != before)
                        {
                            await SaveOrClearJournalAsync(cancellationToken);
                            Volatile.Write(ref _pendingRestoreCount, _savedSessions.Count);
                            _log.Info("audio.restore.retry", $"sessions={attempt.RestoredCount}; pending={_savedSessions.Count}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    _log.LogError("audio.monitor", exception);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void EnsureMonitorStarted()
    {
        if ((_activeTargets.Length > 0 || _savedSessions.Count > 0) && _monitorTask is null)
        {
            _monitorTask = MonitorAsync(_shutdown.Token);
        }
    }

    private async Task<int> CaptureAndMuteAsync(CancellationToken cancellationToken)
    {
        if (_activeTargets.Length == 0) return 0;
        var captured = 0;
        try
        {
            foreach (var session in _backend.Enumerate())
            {
                if (!MatchesPath(session.ExecutablePath, _activeTargets) ||
                    _savedSessions.ContainsKey(session.SessionId)) continue;
                var processStartTime = ProcessAccessInspector.GetProcessStartTimeUtcTicks(session.ProcessId);
                if (processStartTime <= 0) continue;
                _savedSessions[session.SessionId] = new AudioSessionSnapshot
                {
                    SessionId = session.SessionId,
                    ProcessId = session.ProcessId,
                    ProcessStartTimeUtcTicks = processStartTime,
                    ExecutablePath = session.ExecutablePath,
                    Volume = session.Volume,
                    Muted = session.Muted
                };
                captured++;
            }
            if (captured > 0) await SaveOrClearJournalAsync(cancellationToken);
            return MuteRecordedMatchingSessions();
        }
        catch (Exception exception)
        {
            _log.LogError("audio.enumerate", exception);
            return 0;
        }
    }

    private int MuteRecordedMatchingSessions()
    {
        var updates = _backend.Enumerate()
            .Where(session => !session.Muted && _savedSessions.ContainsKey(session.SessionId) &&
                MatchesPath(session.ExecutablePath, _activeTargets))
            .Select(session => new AudioSessionUpdate
            {
                SessionId = session.SessionId,
                Muted = true
            })
            .ToArray();
        var failed = _backend.Apply(updates);
        return updates.Length - failed.Count;
    }

    private RestoreAttempt RestoreSessions(HashSet<string> sessionIds)
    {
        var pending = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        var updates = new List<AudioSessionUpdate>();
        foreach (var session in _backend.Enumerate())
        {
            if (!sessionIds.Contains(session.SessionId) ||
                !_savedSessions.TryGetValue(session.SessionId, out var saved) ||
                saved.ProcessId != session.ProcessId ||
                saved.ProcessStartTimeUtcTicks != ProcessAccessInspector.GetProcessStartTimeUtcTicks(session.ProcessId) ||
                !string.Equals(saved.ExecutablePath, session.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
            updates.Add(new AudioSessionUpdate
            {
                SessionId = session.SessionId,
                Volume = saved.Volume,
                Muted = saved.Muted
            });
        }
        var failed = _backend.Apply(updates);
        foreach (var update in updates.Where(update => !failed.Contains(update.SessionId)))
        {
            pending.Remove(update.SessionId);
        }

        foreach (var sessionId in pending.ToArray())
        {
            if (!_savedSessions.TryGetValue(sessionId, out var saved) ||
                ProcessAccessInspector.GetProcessStartTimeUtcTicks(saved.ProcessId) !=
                    saved.ProcessStartTimeUtcTicks)
            {
                pending.Remove(sessionId);
            }
        }
        return new RestoreAttempt(updates.Count - failed.Count, pending);
    }

    private void RetainOnly(HashSet<string> sessionIds)
    {
        foreach (var id in _savedSessions.Keys.Where(id => !sessionIds.Contains(id)).ToArray())
        {
            _savedSessions.Remove(id);
        }
    }

    private Task SaveOrClearJournalAsync(CancellationToken cancellationToken) =>
        _savedSessions.Count == 0
            ? _recoveryStore.ClearAsync(cancellationToken)
            : _recoveryStore.SaveAsync(new AudioRecoveryState
            {
                Sessions = [.. _savedSessions.Values]
            }, cancellationToken);

    private static bool MatchesPath(string path, IReadOnlyCollection<TargetRule> targets) =>
        targets.Any(target => TargetRuleMatcher.MatchesPath(path, target));

    private sealed record RestoreAttempt(int RestoredCount, HashSet<string> FailedSessionIds);

    private sealed class NullAudioRecoveryStore : IAudioRecoveryStore
    {
        public Task<AudioRecoveryState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AudioRecoveryState());
        public Task SaveAsync(AudioRecoveryState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
