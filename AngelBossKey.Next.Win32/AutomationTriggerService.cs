using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using System.Runtime.InteropServices;

namespace AngelBossKey.Next.Win32;

public sealed class AutomationTriggerService : IAutomationTriggerService
{
    private readonly object _sync = new();
    private readonly IDiagnosticLog _log;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NativeMethods.LowLevelMouseProc _mouseCallback;
    private AutomationSettings _settings = new();
    private nint _mouseHook;
    private readonly TriggerDebouncer _debouncer = new();
    private bool _idleTriggered;
    private bool _disposed;
    private bool _isPaused;

    public AutomationTriggerService(IDiagnosticLog? diagnosticLog = null)
    {
        _log = diagnosticLog ?? NullDiagnosticLog.Instance;
        _mouseCallback = OnMouseEvent;
        _ = MonitorIdleAsync(_shutdown.Token);
    }

    public event EventHandler<AutomationTriggeredEventArgs>? Triggered;

    public bool IsPaused
    {
        get => Volatile.Read(ref _isPaused);
        set => Volatile.Write(ref _isPaused, value);
    }

    public void Configure(AutomationSettings settings)
    {
        lock (_sync)
        {
            _settings = settings with
            {
                IdleMinutes = Math.Clamp(settings.IdleMinutes, 0, 1440),
                CooldownMilliseconds = Math.Clamp(settings.CooldownMilliseconds, 250, 60_000)
            };
            _idleTriggered = false;
            ConfigureMouseHook();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _timer.Dispose();
        lock (_sync)
        {
            RemoveMouseHook();
        }
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task MonitorIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                AutomationSettings settings;
                lock (_sync)
                {
                    settings = _settings;
                }

                if (settings.IdleMinutes <= 0 || IsPaused)
                {
                    _idleTriggered = false;
                    continue;
                }

                var info = new NativeMethods.LastInputInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>()
                };
                if (!NativeMethods.GetLastInputInfo(ref info))
                {
                    continue;
                }

                var idleMilliseconds = unchecked((uint)Environment.TickCount - info.Time);
                var threshold = (uint)TimeSpan.FromMinutes(settings.IdleMinutes).TotalMilliseconds;
                if (idleMilliseconds >= threshold)
                {
                    if (!_idleTriggered)
                    {
                        _idleTriggered = true;
                        RaiseTrigger(settings.CooldownMilliseconds, AutomationTriggerSource.Idle);
                    }
                }
                else
                {
                    _idleTriggered = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.LogError("automation.idle", exception);
        }
    }

    private void ConfigureMouseHook()
    {
        RemoveMouseHook();
        if (!_settings.EnableLowLevelMouseHook || _settings.MouseTrigger == MouseAutomationTrigger.None)
        {
            return;
        }

        _mouseHook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhMouseLl,
            _mouseCallback,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_mouseHook == 0)
        {
            _log.Warning("automation.mouse-hook", "installation-failed=true");
        }
    }

    private nint OnMouseEvent(int code, nint message, nint parameter)
    {
        if (code >= 0 && !IsPaused)
        {
            AutomationSettings settings;
            lock (_sync)
            {
                settings = _settings;
            }

            if (MatchesMouseTrigger(settings.MouseTrigger, (int)message, parameter))
            {
                RaiseTrigger(settings.CooldownMilliseconds, AutomationTriggerSource.Mouse);
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, message, parameter);
    }

    private static bool MatchesMouseTrigger(
        MouseAutomationTrigger trigger,
        int message,
        nint parameter)
    {
        if (trigger == MouseAutomationTrigger.MiddleButton)
        {
            return message == NativeMethods.WmMiddleButtonDown;
        }

        if (message is not (NativeMethods.WmXButtonDown or NativeMethods.WmMouseWheel))
        {
            return false;
        }

        var data = Marshal.PtrToStructure<NativeMethods.LowLevelMouseHookData>(parameter);
        var highWord = unchecked((short)(data.MouseData >> 16));
        return trigger switch
        {
            MouseAutomationTrigger.XButton1 => message == NativeMethods.WmXButtonDown && highWord == 1,
            MouseAutomationTrigger.XButton2 => message == NativeMethods.WmXButtonDown && highWord == 2,
            MouseAutomationTrigger.WheelUp => message == NativeMethods.WmMouseWheel && highWord > 0,
            MouseAutomationTrigger.WheelDown => message == NativeMethods.WmMouseWheel && highWord < 0,
            _ => false
        };
    }

    private void RaiseTrigger(int cooldownMilliseconds, AutomationTriggerSource source)
    {
        var now = Environment.TickCount64;
        if (!_debouncer.TryEnter(now, cooldownMilliseconds))
        {
            return;
        }

        _log.Info("automation.trigger", $"source={source}");
        Triggered?.Invoke(this, new AutomationTriggeredEventArgs(source));
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == 0)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
    }
}

internal sealed class TriggerDebouncer
{
    private readonly object _sync = new();
    private long _lastTriggerTick;
    private bool _hasTriggered;

    internal bool TryEnter(long now, int cooldownMilliseconds)
    {
        lock (_sync)
        {
            if (_hasTriggered && now - _lastTriggerTick < cooldownMilliseconds) return false;
            _lastTriggerTick = now;
            _hasTriggered = true;
            return true;
        }
    }
}
