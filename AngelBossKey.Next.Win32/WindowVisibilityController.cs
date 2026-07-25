using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;

namespace AngelBossKey.Next.Win32;

public sealed partial class WindowVisibilityController(
    IWindowCatalog windowCatalog,
    IRecoveryStore recoveryStore,
    IDiagnosticLog? diagnosticLog = null,
    IElevatedWindowBroker? elevatedBroker = null,
    IWindowNativeActions? nativeActions = null) : IWindowVisibilityController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProcessAccessInspector _accessInspector = new();
    private readonly List<HiddenWindowRecord> _hiddenWindows = [];
    private readonly HashSet<long> _restoreShowSuppressions = [];
    private readonly IDiagnosticLog _log = diagnosticLog ?? NullDiagnosticLog.Instance;
    private readonly IWindowNativeActions _nativeActions = nativeActions ?? new WindowNativeActions();
    private TargetRule[] _activeTargets = [];
    private bool _isHidden;

    public bool IsHidden => Volatile.Read(ref _isHidden);
    public event EventHandler? StateChanged;

    public async Task<VisibilityOperationResult> HideAsync(
        IReadOnlyCollection<TargetRule> targets,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsHidden)
            {
                return new VisibilityOperationResult();
            }

            _activeTargets = targets.Where(target => target.Enabled).ToArray();
            _restoreShowSuppressions.Clear();
            Volatile.Write(ref _isHidden, true);
            var records = new List<HiddenWindowRecord>();
            var skippedElevated = 0;
            var failed = 0;

            foreach (var window in windowCatalog.GetVisibleWindows())
            {
                if (!TargetRuleMatcher.Matches(window, _activeTargets))
                {
                    continue;
                }

                var requiresBroker = _accessInspector.CannotSafelyAccess(window.ProcessId);
                if (requiresBroker && elevatedBroker?.IsEnabled != true)
                {
                    skippedElevated++;
                    continue;
                }

                var record = Capture(window);
                if (record is null)
                {
                    failed++;
                    continue;
                }

                records.Add(record with { RequiresElevatedBroker = requiresBroker });
            }

            _hiddenWindows.Clear();
            _hiddenWindows.AddRange(records);
            try
            {
                await SaveJournalAsync(cancellationToken);
            }
            catch
            {
                _hiddenWindows.Clear();
                _activeTargets = [];
                Volatile.Write(ref _isHidden, false);
                throw;
            }

            var actionRecords = new List<HiddenWindowRecord>();
            string? brokerDetail = null;
            foreach (var record in records.ToArray())
            {
                if (GetWindowIdentity(record, (nint)record.Handle) != WindowIdentityStatus.Same ||
                    !_nativeActions.IsVisible(record.Handle))
                {
                    _hiddenWindows.Remove(record);
                    continue;
                }

                if (!record.RequiresElevatedBroker)
                {
                    _nativeActions.RequestShow(record.Handle, NativeMethods.SwHide);
                }
                actionRecords.Add(record);
            }

            var brokerRecords = actionRecords.Where(record => record.RequiresElevatedBroker).ToArray();
            if (brokerRecords.Length > 0)
            {
                var brokerResult = await ExecuteBrokerAsync(
                    ElevatedWindowCommand.Hide,
                    brokerRecords,
                    cancellationToken);
                if (brokerResult.FailedCount > 0)
                {
                    failed += brokerResult.FailedCount;
                    brokerDetail = brokerResult.Message;
                }
            }

            var confirmedHidden = 0;
            foreach (var record in actionRecords)
            {
                if (!await WaitForVisibilityAsync((nint)record.Handle, visible: false, cancellationToken))
                {
                    if (!record.RequiresElevatedBroker)
                    {
                        failed++;
                    }
                    continue;
                }

                confirmedHidden++;
            }

            if (_hiddenWindows.Count != records.Count)
            {
                await SaveJournalAsync(cancellationToken);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            var result = new VisibilityOperationResult
            {
                ChangedCount = confirmedHidden,
                SkippedElevatedCount = skippedElevated,
                FailedCount = failed,
                Detail = brokerDetail
            };
            _log.Info("windows.hide", FormatResult(result));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VisibilityOperationResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var outcome = await RestoreRecordsAsync(_hiddenWindows, cancellationToken);
            _hiddenWindows.Clear();
            _hiddenWindows.AddRange(outcome.Remaining);
            if (_hiddenWindows.Count == 0)
            {
                _activeTargets = [];
                Volatile.Write(ref _isHidden, false);
                await recoveryStore.ClearAsync(cancellationToken);
            }
            else
            {
                Volatile.Write(ref _isHidden, true);
                await SaveJournalAsync(cancellationToken);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            _log.Info("windows.restore", FormatResult(outcome.Result));
            return outcome.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VisibilityOperationResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await recoveryStore.LoadAsync(cancellationToken);
            var outcome = await RestoreRecordsAsync(state.Windows, cancellationToken);
            _hiddenWindows.Clear();
            _hiddenWindows.AddRange(outcome.Remaining);
            if (_hiddenWindows.Count == 0)
            {
                Volatile.Write(ref _isHidden, false);
                await recoveryStore.ClearAsync(cancellationToken);
            }
            else
            {
                Volatile.Write(ref _isHidden, true);
                await SaveJournalAsync(cancellationToken);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            _log.Info("windows.recover", FormatResult(outcome.Result));
            return outcome.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VisibilityOperationResult> UpdateTargetsAsync(
        IReadOnlyCollection<TargetRule> targets,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _activeTargets = targets.Where(target => target.Enabled).ToArray();
            if (!IsHidden)
            {
                return new VisibilityOperationResult();
            }

            var restoreCandidates = new List<HiddenWindowRecord>();
            foreach (var record in _hiddenWindows)
            {
                var pathRules = _activeTargets
                    .Where(target => TargetRuleMatcher.MatchesPath(record.ExecutablePath, target))
                    .ToArray();
                if (pathRules.Length == 0)
                {
                    restoreCandidates.Add(record);
                    continue;
                }

                var window = windowCatalog.TryGetWindow(record.Handle);
                if (window is not null && !TargetRuleMatcher.Matches(window, pathRules))
                {
                    restoreCandidates.Add(record);
                }
            }

            var restored = new VisibilityOperationResult();
            if (restoreCandidates.Count > 0)
            {
                var candidateHandles = restoreCandidates.Select(record => record.Handle).ToHashSet();
                var retained = _hiddenWindows
                    .Where(record => !candidateHandles.Contains(record.Handle))
                    .ToList();
                var outcome = await RestoreRecordsAsync(restoreCandidates, cancellationToken);
                retained.AddRange(outcome.Remaining);
                _hiddenWindows.Clear();
                _hiddenWindows.AddRange(retained);
                restored = outcome.Result;
            }

            var hidden = await HideMatchingVisibleWindowsAsync(cancellationToken);

            if (_hiddenWindows.Count == 0)
            {
                await recoveryStore.ClearAsync(cancellationToken);
                if (_activeTargets.Length == 0)
                {
                    Volatile.Write(ref _isHidden, false);
                }
            }
            else
            {
                await SaveJournalAsync(cancellationToken);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            var result = restored with
            {
                FailedCount = restored.FailedCount + hidden.FailedCount,
                SkippedElevatedCount = hidden.SkippedElevatedCount
            };
            _log.Info(
                "rules.reconcile",
                $"restored={restored.ChangedCount}; hidden={hidden.ChangedCount}; failed={result.FailedCount}; active={_activeTargets.Length}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VisibilityOperationResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsHidden)
            {
                return new VisibilityOperationResult();
            }

            var corrected = 0;
            var failed = 0;
            var removed = 0;
            foreach (var record in _hiddenWindows.ToArray())
            {
                var window = (nint)record.Handle;
                var identity = GetWindowIdentity(record, window);
                if (identity == WindowIdentityStatus.Different)
                {
                    _hiddenWindows.Remove(record);
                    removed++;
                    continue;
                }
                if (identity == WindowIdentityStatus.Unknown)
                {
                    failed++;
                    continue;
                }
                if (!_nativeActions.IsVisible(record.Handle))
                {
                    continue;
                }

                if (record.RequiresElevatedBroker)
                {
                    await ExecuteBrokerAsync(ElevatedWindowCommand.Hide, [record], cancellationToken);
                }
                else
                {
                    _nativeActions.RequestShow(record.Handle, NativeMethods.SwHide);
                }
                if (await WaitForVisibilityAsync(window, visible: false, cancellationToken))
                {
                    corrected++;
                }
                else
                {
                    failed++;
                }
            }

            if (removed > 0)
            {
                if (_hiddenWindows.Count == 0)
                {
                    await recoveryStore.ClearAsync(cancellationToken);
                    if (_activeTargets.Length == 0)
                    {
                        Volatile.Write(ref _isHidden, false);
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    await SaveJournalAsync(cancellationToken);
                }
            }

            var result = new VisibilityOperationResult { ChangedCount = corrected, FailedCount = failed };
            if (corrected > 0 || failed > 0 || removed > 0)
            {
                _log.Warning("windows.self-check", $"corrected={corrected}; failed={failed}; removed={removed}");
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryHideNewWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_restoreShowSuppressions.Remove(handle))
            {
                return false;
            }

            if (!IsHidden)
            {
                return false;
            }

            var existing = _hiddenWindows.FirstOrDefault(item => item.Handle == handle);
            if (existing is not null)
            {
                var identity = GetWindowIdentity(existing, (nint)handle);
                if (identity == WindowIdentityStatus.Same)
                {
                    if (!_nativeActions.IsVisible(handle))
                    {
                        return false;
                    }

                    _nativeActions.RequestShow(handle, NativeMethods.SwHide);
                    return await WaitForVisibilityAsync((nint)handle, visible: false, cancellationToken);
                }

                if (identity == WindowIdentityStatus.Unknown)
                {
                    return false;
                }

                _hiddenWindows.Remove(existing);
                await SaveJournalAsync(cancellationToken);
            }

            var window = windowCatalog.TryGetWindow(handle);
            if (window is null || !TargetRuleMatcher.Matches(window, _activeTargets))
            {
                return false;
            }

            var requiresBroker = _accessInspector.CannotSafelyAccess(window.ProcessId);
            if (requiresBroker && elevatedBroker?.IsEnabled != true)
            {
                return false;
            }

            var record = Capture(window);
            if (record is null)
            {
                return false;
            }

            record = record with { RequiresElevatedBroker = requiresBroker };
            _hiddenWindows.Add(record);
            await SaveJournalAsync(cancellationToken);
            if (GetWindowIdentity(record, (nint)record.Handle) != WindowIdentityStatus.Same ||
                !_nativeActions.IsVisible(record.Handle))
            {
                _hiddenWindows.Remove(record);
                await SaveJournalAsync(cancellationToken);
                return false;
            }

            if (record.RequiresElevatedBroker)
            {
                await ExecuteBrokerAsync(ElevatedWindowCommand.Hide, [record], cancellationToken);
            }
            else
            {
                _nativeActions.RequestShow(record.Handle, NativeMethods.SwHide);
            }
            if (await WaitForVisibilityAsync((nint)record.Handle, visible: false, cancellationToken))
            {
                return true;
            }

            // The asynchronous hide request may still complete. Keep the recovery
            // record so a later restore cannot strand the window off-screen.
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetDestroyedWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        if (!IsHidden)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var removed = _hiddenWindows.RemoveAll(record => record.Handle == handle);
            if (removed > 0)
            {
                if (_hiddenWindows.Count == 0)
                {
                    await recoveryStore.ClearAsync(cancellationToken);
                    if (_activeTargets.Length == 0)
                    {
                        Volatile.Write(ref _isHidden, false);
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    await SaveJournalAsync(cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VisibilityOperationResult> HideMatchingVisibleWindowsAsync(
        CancellationToken cancellationToken)
    {
        var existingHandles = _hiddenWindows.Select(record => record.Handle).ToHashSet();
        var records = new List<HiddenWindowRecord>();
        var skippedElevated = 0;
        var failed = 0;
        string? brokerDetail = null;
        foreach (var window in windowCatalog.GetVisibleWindows())
        {
            if (existingHandles.Contains(window.Handle) ||
                !TargetRuleMatcher.Matches(window, _activeTargets))
            {
                continue;
            }
            var requiresBroker = _accessInspector.CannotSafelyAccess(window.ProcessId);
            if (requiresBroker && elevatedBroker?.IsEnabled != true)
            {
                skippedElevated++;
                continue;
            }

            var record = Capture(window);
            if (record is null)
            {
                failed++;
                continue;
            }

            record = record with { RequiresElevatedBroker = requiresBroker };
            records.Add(record);
            _hiddenWindows.Add(record);
        }

        if (records.Count == 0)
        {
            return new VisibilityOperationResult
            {
                SkippedElevatedCount = skippedElevated,
                FailedCount = failed
            };
        }

        await SaveJournalAsync(cancellationToken);
        var confirmed = 0;
        var actionRecords = new List<HiddenWindowRecord>();
        foreach (var record in records)
        {
            if (GetWindowIdentity(record, (nint)record.Handle) != WindowIdentityStatus.Same ||
                !_nativeActions.IsVisible(record.Handle))
            {
                _hiddenWindows.Remove(record);
                failed++;
                continue;
            }

            actionRecords.Add(record);
            if (!record.RequiresElevatedBroker)
            {
                _nativeActions.RequestShow(record.Handle, NativeMethods.SwHide);
            }
        }

        var brokerRecords = actionRecords.Where(record => record.RequiresElevatedBroker).ToArray();
        if (brokerRecords.Length > 0)
        {
            var brokerResult = await ExecuteBrokerAsync(
                ElevatedWindowCommand.Hide,
                brokerRecords,
                cancellationToken);
            failed += brokerResult.FailedCount;
            if (brokerResult.FailedCount > 0) brokerDetail = brokerResult.Message;
        }

        foreach (var record in actionRecords)
        {
            if (await WaitForVisibilityAsync((nint)record.Handle, visible: false, cancellationToken))
            {
                confirmed++;
            }
            else
            {
                if (!record.RequiresElevatedBroker)
                {
                    failed++;
                }
            }
        }

        if (_hiddenWindows.Count != existingHandles.Count + records.Count)
        {
            await SaveJournalAsync(cancellationToken);
        }

        return new VisibilityOperationResult
        {
            ChangedCount = confirmed,
            SkippedElevatedCount = skippedElevated,
            FailedCount = failed,
            Detail = brokerDetail
        };
    }

    private async Task<RestoreOutcome> RestoreRecordsAsync(
        IEnumerable<HiddenWindowRecord> records,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        var failed = 0;
        nint foregroundWindow = 0;
        var remaining = new List<HiddenWindowRecord>();
        string? brokerDetail = null;

        var recordList = records.ToList();
        var brokerRecords = new List<HiddenWindowRecord>();
        foreach (var record in recordList.Where(record => record.RequiresElevatedBroker))
        {
            var identity = GetWindowIdentity(record, (nint)record.Handle);
            if (identity == WindowIdentityStatus.Same)
            {
                brokerRecords.Add(record);
            }
            else if (identity == WindowIdentityStatus.Unknown)
            {
                failed++;
                remaining.Add(record);
            }
        }

        if (brokerRecords.Count > 0)
        {
            var response = await ExecuteBrokerAsync(
                ElevatedWindowCommand.Restore,
                brokerRecords,
                cancellationToken);
            foreach (var record in brokerRecords)
            {
                var window = (nint)record.Handle;
                _restoreShowSuppressions.Add(record.Handle);
                if (await WaitForVisibilityAsync(window, visible: true, cancellationToken))
                {
                    changed++;
                    if (record.WasForeground)
                    {
                        foregroundWindow = window;
                    }
                }
                else
                {
                    failed++;
                    remaining.Add(record);
                    _restoreShowSuppressions.Remove(record.Handle);
                }
            }
            if (response.FailedCount > 0)
            {
                brokerDetail = response.Message;
                _log.Warning("broker.restore", $"failed={response.FailedCount}; message={response.Message}");
            }
        }

        foreach (var record in recordList.Where(record => !record.RequiresElevatedBroker))
        {
            var window = (nint)record.Handle;
            var identity = GetWindowIdentity(record, window);
            if (identity == WindowIdentityStatus.Different)
            {
                continue;
            }

            if (identity == WindowIdentityStatus.Unknown)
            {
                failed++;
                remaining.Add(record);
                continue;
            }

            if (!WindowPlacementInterop.TryCreate(
                record.Placement,
                clampToWorkArea: true,
                out var placement))
            {
                failed++;
                remaining.Add(record);
                continue;
            }

            _restoreShowSuppressions.Add(record.Handle);
            if (!NativeMethods.SetWindowPlacement(window, in placement))
            {
                _restoreShowSuppressions.Remove(record.Handle);
                failed++;
                remaining.Add(record);
                continue;
            }

            _nativeActions.RequestShow(record.Handle, (int)placement.ShowCmd);
            if (!await WaitForVisibilityAsync(window, visible: true, cancellationToken))
            {
                _restoreShowSuppressions.Remove(record.Handle);
                failed++;
                if (GetWindowIdentity(record, window) != WindowIdentityStatus.Different)
                {
                    remaining.Add(record);
                }

                continue;
            }

            changed++;
            if (record.WasForeground)
            {
                foregroundWindow = window;
            }
        }

        if (foregroundWindow != 0)
        {
            _ = NativeMethods.SetForegroundWindow(foregroundWindow);
        }

        return new RestoreOutcome(
            new VisibilityOperationResult
            {
                ChangedCount = changed,
                FailedCount = failed,
                Detail = brokerDetail
            },
            remaining);
    }

    private async Task<ElevatedWindowResponse> ExecuteBrokerAsync(
        ElevatedWindowCommand command,
        IReadOnlyCollection<HiddenWindowRecord> records,
        CancellationToken cancellationToken)
    {
        if (elevatedBroker is null)
        {
            return new ElevatedWindowResponse
            {
                FailedCount = records.Count,
                Message = "提权 Broker 不可用。"
            };
        }

        return await elevatedBroker.ExecuteAsync(new ElevatedWindowRequest
        {
            Command = command,
            Windows = [.. records]
        }, cancellationToken);
    }

    private Task SaveJournalAsync(CancellationToken cancellationToken) =>
        recoveryStore.SaveAsync(new RecoveryState { Windows = [.. _hiddenWindows] }, cancellationToken);

}
