using System.IO.Pipes;

namespace AngelBossKey.Next.App.Infrastructure;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\AngelBossKey.Next.Singleton";
    private const string PipeName = "AngelBossKey.Next.Activation";
    private static readonly byte[] ActivationMessage = "activate"u8.ToArray();
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }
    public event EventHandler? ActivationRequested;

    public void StartServer()
    {
        if (!IsPrimary || _serverTask is not null)
        {
            return;
        }

        _serverTask = RunServerAsync(_cancellation.Token);
    }

    public static async Task NotifyPrimaryAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(300);
                await client.WriteAsync(ActivationMessage);
                await client.FlushAsync();
                return;
            }
            catch when (attempt < 7)
            {
                await Task.Delay(200);
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                var buffer = new byte[ActivationMessage.Length];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await server.ReadAsync(buffer.AsMemory(length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }

                if (length == ActivationMessage.Length &&
                    buffer.AsSpan().SequenceEqual(ActivationMessage))
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
