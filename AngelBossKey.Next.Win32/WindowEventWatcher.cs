using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Services;

namespace AngelBossKey.Next.Win32;

public sealed class WindowEventWatcher : IDisposable
{
    private readonly IWindowVisibilityController _visibilityController;
    private readonly IDiagnosticLog _diagnosticLog;
    private readonly NativeMethods.WinEventProc _callback;
    private readonly object _eventSync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<nint, PendingWindowEvents> _pendingEvents = [];
    private nint _hook;
    private bool _disposed;

    public WindowEventWatcher(
        IWindowVisibilityController visibilityController,
        IDiagnosticLog? diagnosticLog = null)
    {
        _visibilityController = visibilityController;
        _diagnosticLog = diagnosticLog ?? NullDiagnosticLog.Instance;
        _callback = OnWindowEvent;
    }

    public void Start()
    {
        var registrationFailed = false;
        lock (_eventSync)
        {
            if (_disposed || _hook != 0)
            {
                return;
            }

            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EventObjectDestroy,
                NativeMethods.EventObjectShow,
                0,
                _callback,
                0,
                0,
                NativeMethods.WineventOutOfContext);
            registrationFailed = _hook == 0;
        }

        if (registrationFailed)
        {
            _diagnosticLog.Warning("windows.hook", "registration-failed=true");
        }
    }

    public void Dispose()
    {
        nint hook;
        lock (_eventSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation.Cancel();
            hook = _hook;
            _hook = 0;
            _pendingEvents.Clear();
        }

        if (hook != 0)
        {
            NativeMethods.UnhookWinEvent(hook);
        }

        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void HandleWindowEvent(uint eventType, long handle) =>
        EnqueueWindowEvent(eventType, (nint)handle);

    private void OnWindowEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (window == 0 || objectId != NativeMethods.ObjidWindow || childId != 0)
        {
            return;
        }

        EnqueueWindowEvent(eventType, window);
    }

    private void EnqueueWindowEvent(uint eventType, nint window)
    {
        if (window == 0 ||
            eventType is not NativeMethods.EventObjectDestroy and not NativeMethods.EventObjectShow)
        {
            return;
        }

        PendingWindowEvents? pendingToStart = null;
        CancellationToken cancellationToken = default;
        lock (_eventSync)
        {
            if (_disposed)
            {
                return;
            }

            if (!_pendingEvents.TryGetValue(window, out var pending))
            {
                pending = new PendingWindowEvents();
                _pendingEvents.Add(window, pending);
                pendingToStart = pending;
                cancellationToken = _cancellation.Token;
            }

            if (eventType == NativeMethods.EventObjectDestroy)
            {
                if (!pending.DestroyInFlight)
                {
                    pending.DestroyPending = true;
                }
                pending.ShowPending = false;
            }
            else if (pending.DestroyPending || (!pending.ShowInFlight && !pending.ShowPending))
            {
                pending.ShowPending = true;
            }
        }

        if (pendingToStart is not null)
        {
            _ = Task.Run(() => ProcessWindowEventsAsync(window, pendingToStart, cancellationToken));
        }
    }

    private async Task ProcessWindowEventsAsync(
        nint window,
        PendingWindowEvents pending,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var eventType = TakePendingEvent(window, pending, cancellationToken);
                if (eventType is null)
                {
                    return;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (eventType == NativeMethods.EventObjectDestroy)
                    {
                        await _visibilityController.ForgetDestroyedWindowAsync((long)window, cancellationToken);
                    }
                    else
                    {
                        await _visibilityController.TryHideNewWindowAsync((long)window, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _diagnosticLog.LogError("windows.event", exception);
                }
                finally
                {
                    if (eventType == NativeMethods.EventObjectDestroy)
                    {
                        CompleteDestroy(window, pending);
                    }
                    else
                    {
                        CompleteShow(window, pending);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _diagnosticLog.LogError("windows.event", exception);
            }
        }
        finally
        {
            lock (_eventSync)
            {
                if (_pendingEvents.TryGetValue(window, out var current) &&
                    ReferenceEquals(current, pending))
                {
                    _pendingEvents.Remove(window);
                }
            }
        }
    }

    private uint? TakePendingEvent(
        nint window,
        PendingWindowEvents pending,
        CancellationToken cancellationToken)
    {
        lock (_eventSync)
        {
            if (_disposed || cancellationToken.IsCancellationRequested ||
                !_pendingEvents.TryGetValue(window, out var current) ||
                !ReferenceEquals(current, pending))
            {
                return null;
            }

            if (pending.DestroyPending)
            {
                pending.DestroyPending = false;
                pending.DestroyInFlight = true;
                return NativeMethods.EventObjectDestroy;
            }

            if (pending.ShowPending)
            {
                pending.ShowPending = false;
                pending.ShowInFlight = true;
                return NativeMethods.EventObjectShow;
            }

            _pendingEvents.Remove(window);
            return null;
        }
    }

    private void CompleteShow(nint window, PendingWindowEvents pending)
    {
        lock (_eventSync)
        {
            if (_pendingEvents.TryGetValue(window, out var current) &&
                ReferenceEquals(current, pending))
            {
                pending.ShowInFlight = false;
            }
        }
    }

    private void CompleteDestroy(nint window, PendingWindowEvents pending)
    {
        lock (_eventSync)
        {
            if (_pendingEvents.TryGetValue(window, out var current) &&
                ReferenceEquals(current, pending))
            {
                pending.DestroyInFlight = false;
            }
        }
    }

    private sealed class PendingWindowEvents
    {
        public bool DestroyPending { get; set; }
        public bool DestroyInFlight { get; set; }
        public bool ShowPending { get; set; }
        public bool ShowInFlight { get; set; }
    }
}
