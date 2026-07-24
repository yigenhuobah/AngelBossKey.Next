using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Services;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace AngelBossKey.Next.Win32;

public sealed class ElevatedWindowBrokerClient(
    bool isEnabled,
    string executablePath,
    IDiagnosticLog? diagnosticLog = null) : IElevatedWindowBroker
{
    private readonly IDiagnosticLog _log = diagnosticLog ?? NullDiagnosticLog.Instance;
    public bool IsEnabled { get; set; } = isEnabled;

    public async Task<ElevatedWindowResponse> ExecuteAsync(
        ElevatedWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new ElevatedWindowResponse
            {
                FailedCount = request.Handles.Count + request.Windows.Count,
                Message = "提权 Broker 未启用，高权限窗口保持可见。"
            };
        }

        var pipeName = $"AngelBossKey.Next.Broker.{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--elevated-broker {pipeName} {token}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _log.Warning("broker.start", $"cancelled-or-denied=true; code={exception.NativeErrorCode}");
            return new ElevatedWindowResponse
            {
                FailedCount = request.Handles.Count + request.Windows.Count,
                Message = exception.NativeErrorCode == 1223
                    ? "用户取消了提权，高权限窗口保持可见。"
                    : $"无法启动提权 Broker：{exception.Message}"
            };
        }

        if (process is null)
        {
            return new ElevatedWindowResponse { FailedCount = 1, Message = "无法启动提权 Broker。" };
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await server.WaitForConnectionAsync(timeout.Token);
                await ElevatedWindowBrokerProtocol.WriteAsync(
                    server,
                    new BrokerEnvelope { Token = token, Request = request },
                    timeout.Token);
                var response = await ElevatedWindowBrokerProtocol.ReadAsync<ElevatedWindowResponse>(
                    server,
                    timeout.Token);
                return response ?? new ElevatedWindowResponse
                {
                    FailedCount = 1,
                    Message = "提权 Broker 返回了无效响应。"
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ElevatedWindowResponse { FailedCount = 1, Message = "提权 Broker 响应超时。" };
            }
            catch (Exception exception)
            {
                _log.LogError("broker.request", exception);
                return new ElevatedWindowResponse { FailedCount = 1, Message = $"提权 Broker 通信失败：{exception.Message}" };
            }
        }
    }

    internal sealed record BrokerEnvelope
    {
        public string Token { get; init; } = string.Empty;
        public ElevatedWindowRequest Request { get; init; } = new();
    }
}

public static class ElevatedWindowBrokerServer
{
    public static async Task<int> RunAsync(
        string pipeName,
        string expectedToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(15_000, cancellationToken);
            var envelope = await ElevatedWindowBrokerProtocol.ReadAsync<ElevatedWindowBrokerClient.BrokerEnvelope>(
                pipe,
                cancellationToken);
            var response = envelope is null ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(envelope.Token),
                    System.Text.Encoding.UTF8.GetBytes(expectedToken))
                ? new ElevatedWindowResponse { FailedCount = 1, Message = "Broker 请求未授权。" }
                : Execute(envelope.Request);
            await ElevatedWindowBrokerProtocol.WriteAsync(pipe, response, cancellationToken);
            return response.FailedCount == 0 ? 0 : 2;
        }
        catch
        {
            return 3;
        }
    }

    private static ElevatedWindowResponse Execute(ElevatedWindowRequest? request)
    {
        var changed = 0;
        var failed = 0;
        if (request is null || !Enum.IsDefined(request.Command) || request.Windows is null ||
            request.Windows.Count is 0 or > 128 || request.Handles is { Count: > 0 })
        {
            return new ElevatedWindowResponse { FailedCount = 1, Message = "Broker 请求格式无效。" };
        }

        var records = request.Windows;

        foreach (var record in records)
        {
            if (record is null)
            {
                failed++;
                continue;
            }
            var window = (nint)record.Handle;
            if (!HasExpectedIdentity(record, window))
            {
                failed++;
                continue;
            }

            var success = request.Command switch
            {
                ElevatedWindowCommand.Query => true,
                ElevatedWindowCommand.Hide => SetVisibility(window, record, visible: false),
                ElevatedWindowCommand.Restore => Restore(window, record),
                _ => false
            };
            if (success)
            {
                changed++;
            }
            else
            {
                failed++;
            }
        }

        return new ElevatedWindowResponse
        {
            ChangedCount = changed,
            FailedCount = failed,
            Message = failed == 0 ? "Broker 操作完成。" : $"Broker 有 {failed} 个窗口操作失败。"
        };
    }

    private static bool Restore(nint window, HiddenWindowRecord record)
    {
        if (!WindowPlacementInterop.TryCreate(record.Placement, clampToWorkArea: true, out var placement))
        {
            return false;
        }
        var placementSet = NativeMethods.SetWindowPlacement(window, in placement);
        _ = NativeMethods.ShowWindowAsync(window, (int)placement.ShowCmd);
        if (record.WasForeground)
        {
            NativeMethods.SetForegroundWindow(window);
        }
        return placementSet && WaitForVisibility(window, record, visible: true);
    }

    private static bool SetVisibility(nint window, HiddenWindowRecord record, bool visible)
    {
        _ = NativeMethods.ShowWindowAsync(window, visible ? NativeMethods.SwShowNormal : NativeMethods.SwHide);
        return WaitForVisibility(window, record, visible);
    }

    private static bool WaitForVisibility(nint window, HiddenWindowRecord record, bool visible)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(1))
        {
            if (!HasExpectedIdentity(record, window)) return false;
            if (NativeMethods.IsWindowVisible(window) == visible) return true;
            Thread.Sleep(15);
        }

        return HasExpectedIdentity(record, window) && NativeMethods.IsWindowVisible(window) == visible;
    }

    private static bool HasExpectedIdentity(HiddenWindowRecord? record, nint window)
    {
        if (record is null || !NativeMethods.IsWindow(window) || record.ProcessId <= 0 ||
            record.ProcessStartTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(record.ExecutablePath) ||
            !ProcessAccessInspector.IsSameUserAndSession(record.ProcessId)) return false;

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId != record.ProcessId) return false;
        if (ProcessAccessInspector.GetProcessStartTimeUtcTicks(record.ProcessId) !=
            record.ProcessStartTimeUtcTicks) return false;
        var path = ProcessPathResolver.TryGetPath(record.ProcessId);
        return string.Equals(path, record.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }
}
