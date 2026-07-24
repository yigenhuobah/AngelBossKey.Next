using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Storage;
using AngelBossKey.Next.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using Forms = System.Windows.Forms;

namespace AngelBossKey.Next.Tests;

public sealed class WindowVisibilityIntegrationTests
{
    [Fact]
    public async Task HotkeyService_ReportsConflictAndCanRegisterAfterRelease()
    {
        using var firstWindow = await TestWindowHost.StartAsync();
        using var secondWindow = await TestWindowHost.StartAsync();
        var firstService = new GlobalHotkeyService();
        var secondService = new GlobalHotkeyService();
        HotkeyGesture? gesture = null;

        try
        {
            (bool Success, string? Error) firstResult = default;
            for (var virtualKey = 0x7C; virtualKey <= 0x87; virtualKey++)
            {
                var candidate = new HotkeyGesture
                {
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt,
                    VirtualKey = virtualKey
                };
                firstResult = firstWindow.Invoke(() =>
                {
                    firstService.AttachWindow((nint)firstWindow.Handle);
                    return (Success: firstService.TryRegister(candidate, out var error), Error: error);
                });
                if (firstResult.Success)
                {
                    gesture = candidate;
                    break;
                }
            }

            Assert.NotNull(gesture);
            var conflictResult = secondWindow.Invoke(() =>
            {
                secondService.AttachWindow((nint)secondWindow.Handle);
                return (Success: secondService.TryRegister(gesture!, out var error), Error: error);
            });

            Assert.True(firstResult.Success, firstResult.Error);
            Assert.False(conflictResult.Success);
            Assert.Contains("占用", conflictResult.Error);

            firstWindow.Invoke(() => firstService.Dispose());
            var retryResult = secondWindow.Invoke(() =>
                (Success: secondService.TryRegister(gesture!, out var error), Error: error));
            Assert.True(retryResult.Success, retryResult.Error);
        }
        finally
        {
            firstWindow.Invoke(() => firstService.Dispose());
            secondWindow.Invoke(() => secondService.Dispose());
        }
    }

    [Fact]
    public async Task HotkeyService_RegistersIndependentHotkeysForMultipleScenes()
    {
        using var window = await TestWindowHost.StartAsync();
        using var service = new GlobalHotkeyService();
        var firstScene = Guid.NewGuid();
        var secondScene = Guid.NewGuid();
        var result = window.Invoke(() =>
        {
            service.AttachWindow((nint)window.Handle);
            var first = service.TryRegister(firstScene, new HotkeyGesture
            {
                Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt,
                VirtualKey = 0x7C
            }, out var firstError);
            var second = service.TryRegister(secondScene, new HotkeyGesture
            {
                Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt,
                VirtualKey = 0x7D
            }, out var secondError);
            return (first, firstError, second, secondError);
        });

        Assert.True(result.first, result.firstError);
        Assert.True(result.second, result.secondError);
    }

    [Fact]
    public async Task Controller_HidesAndRestoresAResponsiveTopLevelWindow()
    {
        using var testWindow = await TestWindowHost.StartAsync();
        var process = Process.GetCurrentProcess();
        var window = new WindowInfo
        {
            Handle = testWindow.Handle,
            ProcessId = process.Id,
            Title = "AngelBossKey integration window",
            ProcessName = process.ProcessName,
            DisplayName = "Integration window",
            ExecutablePath = Environment.ProcessPath!
        };
        var directory = Path.Combine(Path.GetTempPath(), "AngelBossKey.Next.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var controller = new WindowVisibilityController(
                new FixedWindowCatalog(window),
                new JsonRecoveryStore(Path.Combine(directory, "recovery.json")));
            var targets = new[]
            {
                new TargetRule
                {
                    DisplayName = window.DisplayName,
                    ExecutablePath = window.ExecutablePath
                }
            };

            var hideResult = await controller.HideAsync(targets);
            Assert.True(SpinWait.SpinUntil(
                () => !NativeMethods.IsWindowVisible((nint)window.Handle),
                TimeSpan.FromSeconds(3)));

            _ = NativeMethods.ShowWindowAsync((nint)window.Handle, NativeMethods.SwShowNormal);
            Assert.True(SpinWait.SpinUntil(
                () => NativeMethods.IsWindowVisible((nint)window.Handle),
                TimeSpan.FromSeconds(3)));
            Assert.True(await controller.TryHideNewWindowAsync(window.Handle));
            Assert.False(NativeMethods.IsWindowVisible((nint)window.Handle));

            var restoreResult = await controller.RestoreAsync();
            Assert.True(SpinWait.SpinUntil(
                () => NativeMethods.IsWindowVisible((nint)window.Handle),
                TimeSpan.FromSeconds(3)));

            Assert.Equal(1, hideResult.ChangedCount);
            Assert.Equal(1, restoreResult.ChangedCount);
            Assert.False(controller.IsHidden);
            Assert.False(File.Exists(Path.Combine(directory, "recovery.json")));

            for (var cycle = 1; cycle < 100; cycle++)
            {
                hideResult = await controller.HideAsync(targets);
                restoreResult = await controller.RestoreAsync();
                Assert.Equal(1, hideResult.ChangedCount);
                Assert.Equal(1, restoreResult.ChangedCount);
            }

            Assert.True(NativeMethods.IsWindowVisible((nint)window.Handle));
            Assert.False(controller.IsHidden);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task UpdatingRulesWhileHidden_RestoresOnlyTheExcludedWindow()
    {
        using var alphaHost = await TestWindowHost.StartAsync("Alpha workspace");
        using var betaHost = await TestWindowHost.StartAsync("Beta workspace");
        var process = Process.GetCurrentProcess();
        var alpha = CreateWindow(alphaHost, process, "Alpha workspace");
        var beta = CreateWindow(betaHost, process, "Beta workspace");
        var directory = Path.Combine(Path.GetTempPath(), "AngelBossKey.Next.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var controller = new WindowVisibilityController(
                new MultipleWindowCatalog([alpha, beta]),
                new JsonRecoveryStore(Path.Combine(directory, "recovery.json")));
            var alphaRule = new TargetRule
            {
                DisplayName = "Test host",
                ExecutablePath = Environment.ProcessPath!,
                TitleIncludes = "Alpha"
            };
            var betaRule = alphaRule with { Id = Guid.NewGuid(), TitleIncludes = "Beta" };

            var hidden = await controller.HideAsync([alphaRule, betaRule]);
            Assert.Equal(2, hidden.ChangedCount);

            var reconciled = await controller.UpdateTargetsAsync([alphaRule]);

            Assert.Equal(1, reconciled.ChangedCount);
            Assert.False(NativeMethods.IsWindowVisible((nint)alpha.Handle));
            Assert.True(NativeMethods.IsWindowVisible((nint)beta.Handle));

            var restored = await controller.RestoreAsync();
            Assert.Equal(1, restored.ChangedCount);
            Assert.True(NativeMethods.IsWindowVisible((nint)alpha.Handle));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task DelayedAsynchronousHide_KeepsJournalUntilWindowCanBeRestored()
    {
        using var testWindow = await TestWindowHost.StartAsync("Delayed hide");
        var process = Process.GetCurrentProcess();
        var window = CreateWindow(testWindow, process, "Delayed hide");
        var directory = Path.Combine(Path.GetTempPath(), "AngelBossKey.Next.Tests", Guid.NewGuid().ToString("N"));
        var journalPath = Path.Combine(directory, "recovery.json");

        try
        {
            var actions = new ControlledDelayedHideWindowActions();
            var controller = new WindowVisibilityController(
                new FixedWindowCatalog(window),
                new JsonRecoveryStore(journalPath),
                nativeActions: actions);
            var result = await controller.HideAsync(
            [
                new TargetRule { DisplayName = "Delayed", ExecutablePath = window.ExecutablePath }
            ]);

            Assert.Equal(0, result.ChangedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.True(File.Exists(journalPath));

            actions.CompleteHide();
            Assert.True(SpinWait.SpinUntil(
                () => !NativeMethods.IsWindowVisible((nint)window.Handle),
                TimeSpan.FromSeconds(3)));

            var restored = await controller.RestoreAsync();
            Assert.Equal(1, restored.ChangedCount);
            Assert.True(NativeMethods.IsWindowVisible((nint)window.Handle));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BrokerProtocol_ExchangesFramedRequestAndResponseWithoutClosingPipe()
    {
        using var testWindow = await TestWindowHost.StartAsync("Broker protocol");
        var process = Process.GetCurrentProcess();
        var pipeName = $"AngelBossKey.Next.Tests.{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var brokerTask = ElevatedWindowBrokerServer.RunAsync(pipeName, token);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var record = new HiddenWindowRecord
        {
            Handle = testWindow.Handle,
            ProcessId = process.Id,
            ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = Environment.ProcessPath!,
            Placement = new WindowPlacementSnapshot
            {
                ShowCommand = NativeMethods.SwShowNormal,
                Left = 80,
                Top = 80,
                Right = 560,
                Bottom = 400
            }
        };

        await ElevatedWindowBrokerProtocol.WriteAsync(
            server,
            new ElevatedWindowBrokerClient.BrokerEnvelope
            {
                Token = token,
                Request = new ElevatedWindowRequest
                {
                    Command = ElevatedWindowCommand.Query,
                    Windows = [record]
                }
            },
            CancellationToken.None);
        var response = await ElevatedWindowBrokerProtocol.ReadAsync<ElevatedWindowResponse>(
            server,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(1, response.ChangedCount);
        Assert.Equal(0, response.FailedCount);
        Assert.Equal(0, await brokerTask.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Broker_HidesAndRestoresResponsiveWindowAfterAsyncConfirmation()
    {
        using var testWindow = await TestWindowHost.StartAsync("Broker visibility");
        var process = Process.GetCurrentProcess();
        var record = new HiddenWindowRecord
        {
            Handle = testWindow.Handle,
            ProcessId = process.Id,
            ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = Environment.ProcessPath!,
            Placement = new WindowPlacementSnapshot
            {
                ShowCommand = NativeMethods.SwShowNormal,
                Left = 80,
                Top = 80,
                Right = 560,
                Bottom = 400
            }
        };

        var hidden = await ExecuteBrokerRequestAsync(new ElevatedWindowRequest
        {
            Command = ElevatedWindowCommand.Hide,
            Windows = [record]
        });
        Assert.Equal(1, hidden.ChangedCount);
        Assert.Equal(0, hidden.FailedCount);
        Assert.False(NativeMethods.IsWindowVisible((nint)testWindow.Handle));

        var restored = await ExecuteBrokerRequestAsync(new ElevatedWindowRequest
        {
            Command = ElevatedWindowCommand.Restore,
            Windows = [record]
        });
        Assert.Equal(1, restored.ChangedCount);
        Assert.Equal(0, restored.FailedCount);
        Assert.True(NativeMethods.IsWindowVisible((nint)testWindow.Handle));
    }

    private static async Task<ElevatedWindowResponse> ExecuteBrokerRequestAsync(ElevatedWindowRequest request)
    {
        var pipeName = $"AngelBossKey.Next.Tests.{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var brokerTask = ElevatedWindowBrokerServer.RunAsync(pipeName, token);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await ElevatedWindowBrokerProtocol.WriteAsync(
            server,
            new ElevatedWindowBrokerClient.BrokerEnvelope { Token = token, Request = request },
            CancellationToken.None);
        var response = await ElevatedWindowBrokerProtocol.ReadAsync<ElevatedWindowResponse>(
            server,
            CancellationToken.None);
        Assert.Equal(0, await brokerTask.WaitAsync(TimeSpan.FromSeconds(3)));
        return Assert.IsType<ElevatedWindowResponse>(response);
    }

    private static WindowInfo CreateWindow(TestWindowHost host, Process process, string title) => new()
    {
        Handle = host.Handle,
        ProcessId = process.Id,
        Title = title,
        ProcessName = process.ProcessName,
        DisplayName = "Test host",
        ExecutablePath = Environment.ProcessPath!
    };

    private sealed class FixedWindowCatalog(WindowInfo window) : IWindowCatalog
    {
        public IReadOnlyList<WindowInfo> GetVisibleWindows() => [window];
        public WindowInfo? TryGetWindow(long handle) => handle == window.Handle ? window : null;
    }

    private sealed class MultipleWindowCatalog(IReadOnlyList<WindowInfo> windows) : IWindowCatalog
    {
        public IReadOnlyList<WindowInfo> GetVisibleWindows() =>
            windows.Where(window => NativeMethods.IsWindowVisible((nint)window.Handle)).ToArray();

        public WindowInfo? TryGetWindow(long handle) => windows.FirstOrDefault(window => window.Handle == handle);
    }

    private sealed class ControlledDelayedHideWindowActions : IWindowNativeActions
    {
        private readonly TaskCompletionSource _hideRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Exists(long handle) => NativeMethods.IsWindow((nint)handle);
        public bool IsVisible(long handle) => NativeMethods.IsWindowVisible((nint)handle);
        public void CompleteHide() => _hideRequested.TrySetResult();
        public void RequestShow(long handle, int command)
        {
            if (command != NativeMethods.SwHide)
            {
                _ = NativeMethods.ShowWindowAsync((nint)handle, command);
                return;
            }

            _ = Task.Run(async () =>
            {
                await _hideRequested.Task;
                _ = NativeMethods.ShowWindowAsync((nint)handle, command);
            });
        }
    }

    private sealed class TestWindowHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly Forms.Form _form;

        private TestWindowHost(Thread thread, Forms.Form form, nint handle)
        {
            _thread = thread;
            _form = form;
            Handle = handle;
        }

        public long Handle { get; }

        public T Invoke<T>(Func<T> action) => (T)_form.Invoke(action);

        public void Invoke(Action action) => _form.Invoke(action);

        public static async Task<TestWindowHost> StartAsync(string title = "AngelBossKey integration window")
        {
            var ready = new TaskCompletionSource<(Forms.Form Form, nint Handle)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var form = new Forms.Form
                {
                    Text = title,
                    Width = 480,
                    Height = 320,
                    StartPosition = Forms.FormStartPosition.Manual,
                    Left = 80,
                    Top = 80
                };
                form.Shown += (_, _) => ready.TrySetResult((form, form.Handle));
                Forms.Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new TestWindowHost(thread, result.Form, result.Handle);
        }

        public void Dispose()
        {
            if (!_form.IsDisposed)
            {
                _form.BeginInvoke(_form.Close);
            }

            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }
}
