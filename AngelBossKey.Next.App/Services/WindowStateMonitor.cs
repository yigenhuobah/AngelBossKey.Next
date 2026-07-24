using AngelBossKey.Next.Core.Abstractions;

namespace AngelBossKey.Next.App.Services;

public sealed class WindowStateMonitor(
    IWindowVisibilityController visibilityController,
    IDiagnosticLog diagnosticLog) : IDisposable
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await visibilityController.SelfCheckAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            diagnosticLog.Error("windows.monitor", exception);
        }
    }
}
