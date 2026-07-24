using AngelBossKey.Next.Core.Abstractions;

namespace AngelBossKey.Next.App.Services;

public sealed class WindowStateMonitor(
    IWindowVisibilityController visibilityController,
    IDiagnosticLog diagnosticLog,
    TimeSpan? interval = null) : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _monitorTask;

    public void Start()
    {
        _monitorTask ??= MonitorAsync(_cancellation.Token);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await visibilityController.SelfCheckAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    diagnosticLog.LogError("windows.monitor", exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
