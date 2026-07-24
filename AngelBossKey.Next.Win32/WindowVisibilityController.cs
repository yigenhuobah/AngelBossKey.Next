using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;

namespace AngelBossKey.Next.Win32;

public sealed class WindowVisibilityController(
    IWindowCatalog windowCatalog,
    IRecoveryStore recoveryStore,
    IDiagnosticLog? diagnosticLog = null) : IWindowVisibilityController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProcessAccessInspector _accessInspector = new();
    private readonly List<HiddenWindowRecord> _hiddenWindows = [];
    private readonly HashSet<long> _restoreShowSuppressions = [];
    private readonly IDiagnosticLog _log = diagnosticLog ?? NullDiagnosticLog.Instance;
    private IReadOnlyCollection<TargetRule> _activeTargets = [];
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

                if (_accessInspector.CannotSafelyAccess(window.ProcessId))
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

                records.Add(record);
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
            foreach (var record in records.ToArray())
            {
                if (GetWindowIdentity(record, (nint)record.Handle) != WindowIdentityStatus.Same ||
                    !NativeMethods.IsWindowVisible((nint)record.Handle))
                {
                    _hiddenWindows.Remove(record);
                    continue;
                }

                _ = NativeMethods.ShowWindowAsync((nint)record.Handle, NativeMethods.SwHide);
                actionRecords.Add(record);
            }

            var confirmedHidden = 0;
            foreach (var record in actionRecords)
            {
                if (!await WaitForVisibilityAsync((nint)record.Handle, visible: false, cancellationToken))
                {
                    failed++;
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
                FailedCount = failed
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
                if (_activeTargets.Count == 0)
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
                $"restored={restored.ChangedCount}; hidden={hidden.ChangedCount}; failed={result.FailedCount}; active={_activeTargets.Count}");
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
                if (!NativeMethods.IsWindowVisible(window))
                {
                    continue;
                }

                _ = NativeMethods.ShowWindowAsync(window, NativeMethods.SwHide);
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
                    if (_activeTargets.Count == 0)
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
                    if (!NativeMethods.IsWindowVisible((nint)handle))
                    {
                        return false;
                    }

                    _ = NativeMethods.ShowWindowAsync((nint)handle, NativeMethods.SwHide);
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
            if (window is null ||
                !TargetRuleMatcher.Matches(window, _activeTargets) ||
                _accessInspector.CannotSafelyAccess(window.ProcessId))
            {
                return false;
            }

            var record = Capture(window);
            if (record is null)
            {
                return false;
            }

            _hiddenWindows.Add(record);
            await SaveJournalAsync(cancellationToken);
            if (GetWindowIdentity(record, (nint)record.Handle) != WindowIdentityStatus.Same ||
                !NativeMethods.IsWindowVisible((nint)record.Handle))
            {
                _hiddenWindows.Remove(record);
                await SaveJournalAsync(cancellationToken);
                return false;
            }

            _ = NativeMethods.ShowWindowAsync((nint)record.Handle, NativeMethods.SwHide);
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
                    if (_activeTargets.Count == 0)
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
        foreach (var window in windowCatalog.GetVisibleWindows())
        {
            if (existingHandles.Contains(window.Handle) ||
                !TargetRuleMatcher.Matches(window, _activeTargets))
            {
                continue;
            }
            if (_accessInspector.CannotSafelyAccess(window.ProcessId))
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
        foreach (var record in records)
        {
            if (GetWindowIdentity(record, (nint)record.Handle) != WindowIdentityStatus.Same ||
                !NativeMethods.IsWindowVisible((nint)record.Handle))
            {
                _hiddenWindows.Remove(record);
                failed++;
                continue;
            }

            _ = NativeMethods.ShowWindowAsync((nint)record.Handle, NativeMethods.SwHide);
            if (await WaitForVisibilityAsync((nint)record.Handle, visible: false, cancellationToken))
            {
                confirmed++;
            }
            else
            {
                failed++;
            }
        }

        return new VisibilityOperationResult
        {
            ChangedCount = confirmed,
            SkippedElevatedCount = skippedElevated,
            FailedCount = failed
        };
    }

    private HiddenWindowRecord? Capture(WindowInfo window)
    {
        var placement = new NativeMethods.WindowPlacement
        {
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>()
        };
        if (!NativeMethods.GetWindowPlacement((nint)window.Handle, ref placement))
        {
            return null;
        }

        var processStartTime = ProcessAccessInspector.GetProcessStartTimeUtcTicks(window.ProcessId);
        if (processStartTime <= 0)
        {
            return null;
        }

        return new HiddenWindowRecord
        {
            Handle = window.Handle,
            ProcessId = window.ProcessId,
            ProcessStartTimeUtcTicks = processStartTime,
            ExecutablePath = window.ExecutablePath,
            Placement = new WindowPlacementSnapshot
            {
                Flags = (int)placement.Flags,
                ShowCommand = (int)placement.ShowCmd,
                MinPositionX = placement.MinPosition.X,
                MinPositionY = placement.MinPosition.Y,
                MaxPositionX = placement.MaxPosition.X,
                MaxPositionY = placement.MaxPosition.Y,
                Left = placement.NormalPosition.Left,
                Top = placement.NormalPosition.Top,
                Right = placement.NormalPosition.Right,
                Bottom = placement.NormalPosition.Bottom
            },
            WasForeground = NativeMethods.GetForegroundWindow() == (nint)window.Handle
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

        foreach (var record in records)
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

            var placement = new NativeMethods.WindowPlacement
            {
                Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>(),
                Flags = (uint)record.Placement.Flags,
                ShowCmd = (uint)Math.Max(record.Placement.ShowCommand, NativeMethods.SwShowNormal),
                MinPosition = new NativeMethods.Point
                {
                    X = record.Placement.MinPositionX,
                    Y = record.Placement.MinPositionY
                },
                MaxPosition = new NativeMethods.Point
                {
                    X = record.Placement.MaxPositionX,
                    Y = record.Placement.MaxPositionY
                },
                NormalPosition = new NativeMethods.Rect
                {
                    Left = record.Placement.Left,
                    Top = record.Placement.Top,
                    Right = record.Placement.Right,
                    Bottom = record.Placement.Bottom
                }
            };
            ClampToVisibleWorkArea(ref placement);

            _restoreShowSuppressions.Add(record.Handle);
            if (!NativeMethods.SetWindowPlacement(window, in placement))
            {
                _restoreShowSuppressions.Remove(record.Handle);
                failed++;
                remaining.Add(record);
                continue;
            }

            _ = NativeMethods.ShowWindowAsync(window, (int)placement.ShowCmd);
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
            new VisibilityOperationResult { ChangedCount = changed, FailedCount = failed },
            remaining);
    }

    private static WindowIdentityStatus GetWindowIdentity(HiddenWindowRecord record, nint window)
    {
        if (!NativeMethods.IsWindow(window))
        {
            return WindowIdentityStatus.Different;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId != record.ProcessId || record.ProcessStartTimeUtcTicks <= 0)
        {
            return WindowIdentityStatus.Different;
        }

        var processStartTime = ProcessAccessInspector.GetProcessStartTimeUtcTicks(record.ProcessId);
        if (processStartTime <= 0)
        {
            return WindowIdentityStatus.Unknown;
        }
        if (processStartTime != record.ProcessStartTimeUtcTicks)
        {
            return WindowIdentityStatus.Different;
        }

        var executablePath = WindowCatalog.GetProcessPath(processId);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return WindowIdentityStatus.Unknown;
        }

        return string.Equals(executablePath, record.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            ? WindowIdentityStatus.Same
            : WindowIdentityStatus.Different;
    }

    private Task SaveJournalAsync(CancellationToken cancellationToken) =>
        recoveryStore.SaveAsync(new RecoveryState { Windows = [.. _hiddenWindows] }, cancellationToken);

    private static async Task<bool> WaitForVisibilityAsync(
        nint window,
        bool visible,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!NativeMethods.IsWindow(window))
            {
                return false;
            }

            if (NativeMethods.IsWindowVisible(window) == visible)
            {
                return true;
            }

            await Task.Delay(25, cancellationToken);
        }

        return NativeMethods.IsWindow(window) && NativeMethods.IsWindowVisible(window) == visible;
    }

    private static void ClampToVisibleWorkArea(ref NativeMethods.WindowPlacement placement)
    {
        var rectangle = placement.NormalPosition;
        var width = Math.Max(100, rectangle.Right - rectangle.Left);
        var height = Math.Max(80, rectangle.Bottom - rectangle.Top);
        var monitor = NativeMethods.MonitorFromRect(in rectangle, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return;
        }

        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
        {
            return;
        }

        var work = monitorInfo.WorkArea;
        width = Math.Min(width, Math.Max(100, work.Right - work.Left));
        height = Math.Min(height, Math.Max(80, work.Bottom - work.Top));
        var left = Math.Clamp(rectangle.Left, work.Left, Math.Max(work.Left, work.Right - width));
        var top = Math.Clamp(rectangle.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        placement.NormalPosition = new NativeMethods.Rect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
    }

    private static string FormatResult(VisibilityOperationResult result) =>
        $"changed={result.ChangedCount}; failed={result.FailedCount}; elevated={result.SkippedElevatedCount}";

    private sealed record RestoreOutcome(
        VisibilityOperationResult Result,
        IReadOnlyList<HiddenWindowRecord> Remaining);

    private enum WindowIdentityStatus
    {
        Same,
        Different,
        Unknown
    }
}
