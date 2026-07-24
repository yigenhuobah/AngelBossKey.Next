using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Services;

namespace AngelBossKey.Next.Win32;

public sealed class WindowEventWatcher : IDisposable
{
    private readonly IWindowVisibilityController _visibilityController;
    private readonly IDiagnosticLog _diagnosticLog;
    private readonly NativeMethods.WinEventProc _callback;
    private readonly object _eventSync = new();
    private nint _hook;
    private Task _eventTail = Task.CompletedTask;

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
        if (_hook != 0)
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
        if (_hook == 0)
        {
            _diagnosticLog.Warning("windows.hook", "registration-failed=true");
        }
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
        }

        GC.SuppressFinalize(this);
    }

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

        lock (_eventSync)
        {
            _eventTail = ProcessAfterAsync(_eventTail, eventType, window);
        }
    }

    private async Task ProcessAfterAsync(Task previous, uint eventType, nint window)
    {
        try
        {
            await previous;
        }
        catch
        {
        }

        try
        {
            if (eventType == NativeMethods.EventObjectDestroy)
            {
                await _visibilityController.ForgetDestroyedWindowAsync(window);
            }
            else if (eventType == NativeMethods.EventObjectShow)
            {
                await _visibilityController.TryHideNewWindowAsync(window);
            }
        }
        catch (Exception exception)
        {
            _diagnosticLog.LogError("windows.event", exception);
        }
    }
}
