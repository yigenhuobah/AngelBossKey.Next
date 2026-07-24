using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Storage;
using AngelBossKey.Next.Win32;
using System.Diagnostics;
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

    private sealed class FixedWindowCatalog(WindowInfo window) : IWindowCatalog
    {
        public IReadOnlyList<WindowInfo> GetVisibleWindows() => [window];
        public WindowInfo? TryGetWindow(long handle) => handle == window.Handle ? window : null;
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

        public static async Task<TestWindowHost> StartAsync()
        {
            var ready = new TaskCompletionSource<(Forms.Form Form, nint Handle)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var form = new Forms.Form
                {
                    Text = "AngelBossKey integration window",
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
