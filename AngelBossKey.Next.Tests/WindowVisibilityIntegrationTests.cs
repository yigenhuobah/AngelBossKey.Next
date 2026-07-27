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
    public async Task WindowEventWatcher_ProcessesDifferentWindowsWithoutGlobalBacklogAndStopsAfterDispose()
    {
        var controller = new EventProbeVisibilityController();
        using var watcher = new WindowEventWatcher(controller);

        watcher.HandleWindowEvent(NativeMethods.EventObjectShow, EventProbeVisibilityController.BlockingShowHandle);
        await controller.BlockingShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        watcher.HandleWindowEvent(NativeMethods.EventObjectShow, EventProbeVisibilityController.SecondShowHandle);
        await controller.SecondShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        watcher.HandleWindowEvent(NativeMethods.EventObjectShow, EventProbeVisibilityController.BlockingShowHandle);
        watcher.Dispose();
        await controller.BlockingShowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        watcher.HandleWindowEvent(NativeMethods.EventObjectShow, 303);
        await Task.Yield();

        Assert.Equal(1, controller.GetShowCallCount(EventProbeVisibilityController.BlockingShowHandle));
        Assert.Equal(1, controller.GetShowCallCount(EventProbeVisibilityController.SecondShowHandle));
        Assert.Equal(0, controller.GetShowCallCount(303));
    }

    [Fact]
    public async Task WindowEventWatcher_PreservesShowAfterDestroyForTheSameHandle()
    {
        var controller = new EventProbeVisibilityController();
        using var watcher = new WindowEventWatcher(controller);

        watcher.HandleWindowEvent(NativeMethods.EventObjectDestroy, EventProbeVisibilityController.ReusedHandle);
        await controller.DestroyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        watcher.HandleWindowEvent(NativeMethods.EventObjectShow, EventProbeVisibilityController.ReusedHandle);
        controller.ReleaseDestroy();
        await controller.ReusedHandleShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["destroy", "show"], controller.GetReusedHandleOperations());
    }

    [Fact]
    public async Task WindowEventWatcher_CoalescesDuplicateDestroyEvents()
    {
        var controller = new EventProbeVisibilityController();
        using var watcher = new WindowEventWatcher(controller);

        watcher.HandleWindowEvent(NativeMethods.EventObjectDestroy, EventProbeVisibilityController.ReusedHandle);
        await controller.DestroyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        watcher.HandleWindowEvent(NativeMethods.EventObjectDestroy, EventProbeVisibilityController.ReusedHandle);
        controller.ReleaseDestroy();
        await controller.DestroyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        watcher.HandleWindowEvent(NativeMethods.EventObjectShow, EventProbeVisibilityController.ReusedHandle);
        await controller.ReusedHandleShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, controller.GetDestroyCallCount());
        Assert.False(controller.SecondDestroyStarted.Task.IsCompleted);
    }

    [Fact]
    [Trait("Category", "Reliability")]
    public async Task Controller_HidesAndRestoresAResponsiveTopLevelWindow()
    {
        using var testWindow = await TestWindowHost.StartAsync();
        using var process = Process.GetCurrentProcess();
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

            for (var cycle = 1; cycle < ReliabilityTestSettings.WindowVisibilityCycles; cycle++)
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
    public async Task Controller_RestoreShowSuppression_CoalescesEventsUntilExpiry()
    {
        using var restoredHost = await TestWindowHost.StartAsync("Suppressed restore window");
        using var pendingHost = await TestWindowHost.StartAsync("Pending restore window");
        using var process = Process.GetCurrentProcess();
        var restoredWindow = CreateWindow(restoredHost, process, "Suppressed restore window");
        var pendingWindow = CreateWindow(pendingHost, process, "Pending restore window");
        var directory = Path.Combine(Path.GetTempPath(), "AngelBossKey.Next.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var actions = new RestoreSuppressionWindowActions(pendingWindow.Handle);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var controller = new WindowVisibilityController(
                new MultipleWindowCatalog([restoredWindow, pendingWindow]),
                new JsonRecoveryStore(Path.Combine(directory, "recovery.json")),
                nativeActions: actions,
                timeProvider: clock);
            var target = new TargetRule
            {
                DisplayName = "Test host",
                ExecutablePath = Environment.ProcessPath!
            };

            var hidden = await controller.HideAsync([target]);
            Assert.Equal(2, hidden.ChangedCount);

            actions.MakeRestoreWaitFailForPendingWindow();
            var restored = await controller.RestoreAsync();
            Assert.Equal(1, restored.ChangedCount);
            Assert.Equal(1, restored.FailedCount);
            Assert.True(controller.IsHidden);
            Assert.True(NativeMethods.IsWindowVisible((nint)restoredWindow.Handle));

            var hideRequestsBeforeEvents = actions.GetHideRequestCount(restoredWindow.Handle);
            Assert.False(await controller.TryHideNewWindowAsync(restoredWindow.Handle));
            Assert.False(await controller.TryHideNewWindowAsync(restoredWindow.Handle));
            Assert.Equal(hideRequestsBeforeEvents, actions.GetHideRequestCount(restoredWindow.Handle));

            clock.Advance(TimeSpan.FromSeconds(2));

            Assert.True(await controller.TryHideNewWindowAsync(restoredWindow.Handle));
            Assert.Equal(hideRequestsBeforeEvents + 1, actions.GetHideRequestCount(restoredWindow.Handle));
            Assert.True(SpinWait.SpinUntil(
                () => !NativeMethods.IsWindowVisible((nint)restoredWindow.Handle),
                TimeSpan.FromSeconds(3)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Controller_RestoreShowSuppression_DropsStaleWindowIdentity()
    {
        using var restoredHost = await TestWindowHost.StartAsync("Stale identity restore window");
        using var pendingHost = await TestWindowHost.StartAsync("Pending identity restore window");
        using var process = Process.GetCurrentProcess();
        var restoredWindow = CreateWindow(restoredHost, process, "Stale identity restore window");
        var pendingWindow = CreateWindow(pendingHost, process, "Pending identity restore window");
        var directory = Path.Combine(Path.GetTempPath(), "AngelBossKey.Next.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var actions = new RestoreSuppressionWindowActions(pendingWindow.Handle);
            var controller = new WindowVisibilityController(
                new MultipleWindowCatalog([restoredWindow, pendingWindow]),
                new JsonRecoveryStore(Path.Combine(directory, "recovery.json")),
                nativeActions: actions,
                timeProvider: new MutableTimeProvider(DateTimeOffset.UtcNow));
            var target = new TargetRule
            {
                DisplayName = "Test host",
                ExecutablePath = Environment.ProcessPath!
            };

            Assert.Equal(2, (await controller.HideAsync([target])).ChangedCount);
            actions.MakeRestoreWaitFailForPendingWindow();
            Assert.Equal(1, (await controller.RestoreAsync()).ChangedCount);
            Assert.True(controller.IsHidden);

            var hideRequestsBeforeEvent = actions.GetHideRequestCount(restoredWindow.Handle);
            actions.InvalidateNextIdentityCheck(restoredWindow.Handle);

            Assert.True(await controller.TryHideNewWindowAsync(restoredWindow.Handle));
            Assert.Equal(hideRequestsBeforeEvent + 1, actions.GetHideRequestCount(restoredWindow.Handle));
            Assert.True(SpinWait.SpinUntil(
                () => !NativeMethods.IsWindowVisible((nint)restoredWindow.Handle),
                TimeSpan.FromSeconds(3)));
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
        var record = CreateBrokerRecord(testWindow, process);

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
    public async Task BrokerProtocol_RejectsRequestWithAnInvalidToken()
    {
        using var testWindow = await TestWindowHost.StartAsync("Broker invalid token");
        using var process = Process.GetCurrentProcess();
        var pipeName = $"AngelBossKey.Next.Tests.{Guid.NewGuid():N}";
        const string expectedToken = "expected-token";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var brokerTask = ElevatedWindowBrokerServer.RunAsync(
            pipeName,
            expectedToken,
            TestContext.Current.CancellationToken);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ElevatedWindowBrokerProtocol.WriteAsync(
            server,
            new ElevatedWindowBrokerClient.BrokerEnvelope
            {
                Token = "invalid-token-",
                Request = new ElevatedWindowRequest
                {
                    Command = ElevatedWindowCommand.Query,
                    Windows = [CreateBrokerRecord(testWindow, process)]
                }
            },
            TestContext.Current.CancellationToken);

        var response = await ElevatedWindowBrokerProtocol.ReadAsync<ElevatedWindowResponse>(
            server,
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal(0, response.ChangedCount);
        Assert.Equal(1, response.FailedCount);
        Assert.Equal(2, await brokerTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BrokerProtocol_RejectsLegacyHandleOnlyRequest()
    {
        using var testWindow = await TestWindowHost.StartAsync("Broker legacy handle");
        using var process = Process.GetCurrentProcess();
        var result = await ExecuteBrokerRequestWithExitCodeAsync(new ElevatedWindowRequest
        {
            Command = ElevatedWindowCommand.Query,
            Handles = [123],
            Windows = [CreateBrokerRecord(testWindow, process)]
        });

        Assert.Equal(0, result.Response.ChangedCount);
        Assert.Equal(1, result.Response.FailedCount);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Broker_HidesAndRestoresResponsiveWindowAfterAsyncConfirmation()
    {
        using var testWindow = await TestWindowHost.StartAsync("Broker visibility");
        using var process = Process.GetCurrentProcess();
        var record = CreateBrokerRecord(testWindow, process);

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
        var result = await ExecuteBrokerRequestWithExitCodeAsync(request);
        Assert.Equal(0, result.ExitCode);
        return result.Response;
    }

    private static async Task<(ElevatedWindowResponse Response, int ExitCode)> ExecuteBrokerRequestWithExitCodeAsync(
        ElevatedWindowRequest request)
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
        var exitCode = await brokerTask.WaitAsync(TimeSpan.FromSeconds(3));
        return (Assert.IsType<ElevatedWindowResponse>(response), exitCode);
    }

    private static HiddenWindowRecord CreateBrokerRecord(TestWindowHost testWindow, Process process) => new()
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

    private sealed class RestoreSuppressionWindowActions(long pendingWindowHandle) : IWindowNativeActions
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, int> _hideRequestCounts = [];
        private bool _makePendingWindowAppearInvisible;
        private long? _invalidatedHandle;

        public bool Exists(long handle)
        {
            lock (_sync)
            {
                if (_invalidatedHandle == handle)
                {
                    _invalidatedHandle = null;
                    return false;
                }
            }

            return NativeMethods.IsWindow((nint)handle);
        }

        public bool IsVisible(long handle)
        {
            lock (_sync)
            {
                if (_makePendingWindowAppearInvisible && handle == pendingWindowHandle)
                {
                    return false;
                }
            }

            return NativeMethods.IsWindowVisible((nint)handle);
        }

        public void RequestShow(long handle, int command)
        {
            if (command == NativeMethods.SwHide)
            {
                lock (_sync)
                {
                    _hideRequestCounts.TryGetValue(handle, out var count);
                    _hideRequestCounts[handle] = count + 1;
                }
            }

            _ = NativeMethods.ShowWindowAsync((nint)handle, command);
        }

        public void MakeRestoreWaitFailForPendingWindow()
        {
            lock (_sync)
            {
                _makePendingWindowAppearInvisible = true;
            }
        }

        public void InvalidateNextIdentityCheck(long handle)
        {
            lock (_sync)
            {
                _invalidatedHandle = handle;
            }
        }

        public int GetHideRequestCount(long handle)
        {
            lock (_sync)
            {
                return _hideRequestCounts.TryGetValue(handle, out var count) ? count : 0;
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_sync)
            {
                _utcNow += duration;
            }
        }
    }

    private sealed class EventProbeVisibilityController : IWindowVisibilityController
    {
        public const long BlockingShowHandle = 101;
        public const long SecondShowHandle = 202;
        public const long ReusedHandle = 404;

        private readonly object _sync = new();
        private readonly Dictionary<long, int> _showCalls = [];
        private readonly List<string> _reusedHandleOperations = [];
        private readonly TaskCompletionSource _releaseBlockingShow =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDestroy =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _destroyCalls;

        public TaskCompletionSource BlockingShowStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockingShowCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondShowStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DestroyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DestroyCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDestroyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReusedHandleShowStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsHidden => false;
        public event EventHandler? StateChanged { add { } remove { } }

        public Task<VisibilityOperationResult> HideAsync(
            IReadOnlyCollection<TargetRule> targets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<VisibilityOperationResult> RestoreAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<VisibilityOperationResult> RecoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<VisibilityOperationResult> UpdateTargetsAsync(
            IReadOnlyCollection<TargetRule> targets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public Task<VisibilityOperationResult> SelfCheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VisibilityOperationResult());

        public async Task<bool> TryHideNewWindowAsync(long handle, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _showCalls.TryGetValue(handle, out var count);
                _showCalls[handle] = count + 1;
                if (handle == ReusedHandle)
                {
                    _reusedHandleOperations.Add("show");
                }
            }

            if (handle == BlockingShowHandle)
            {
                BlockingShowStarted.TrySetResult();
                try
                {
                    await _releaseBlockingShow.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    BlockingShowCancelled.TrySetResult();
                    throw;
                }
            }
            else if (handle == SecondShowHandle)
            {
                SecondShowStarted.TrySetResult();
            }
            else if (handle == ReusedHandle)
            {
                ReusedHandleShowStarted.TrySetResult();
            }

            return true;
        }

        public async Task ForgetDestroyedWindowAsync(long handle, CancellationToken cancellationToken = default)
        {
            if (handle != ReusedHandle)
            {
                return;
            }

            int destroyCall;
            lock (_sync)
            {
                _reusedHandleOperations.Add("destroy");
                destroyCall = ++_destroyCalls;
            }

            if (destroyCall > 1)
            {
                SecondDestroyStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            DestroyStarted.TrySetResult();
            await _releaseDestroy.Task.WaitAsync(cancellationToken);
            DestroyCompleted.TrySetResult();
        }

        public int GetShowCallCount(long handle)
        {
            lock (_sync)
            {
                return _showCalls.TryGetValue(handle, out var count) ? count : 0;
            }
        }

        public IReadOnlyList<string> GetReusedHandleOperations()
        {
            lock (_sync)
            {
                return [.. _reusedHandleOperations];
            }
        }

        public int GetDestroyCallCount()
        {
            lock (_sync)
            {
                return _destroyCalls;
            }
        }

        public void ReleaseDestroy() => _releaseDestroy.TrySetResult();
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
