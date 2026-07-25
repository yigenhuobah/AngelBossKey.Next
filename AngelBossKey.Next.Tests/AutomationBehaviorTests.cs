using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Win32;
using System.Diagnostics;

namespace AngelBossKey.Next.Tests;

public sealed class AutomationBehaviorTests
{
    [Fact]
    public void TriggerDebouncer_AppliesCooldownAtTheBoundary()
    {
        var debouncer = new TriggerDebouncer();

        Assert.True(debouncer.TryEnter(1_000, 1_000));
        Assert.False(debouncer.TryEnter(1_999, 1_000));
        Assert.True(debouncer.TryEnter(2_000, 1_000));
    }

    [Fact]
    public async Task PrivacyDesktop_ReturnWhileInactiveIsNonDestructive()
    {
        using var desktop = new PrivacyDesktopService();

        var result = await desktop.ReturnAsync();

        Assert.True(result.Success);
        Assert.False(desktop.IsActive);
    }

    [Fact]
    public void DesktopShell_QuotesApplicationPathAndPassesReturnThread()
    {
        var command = DesktopShellHost.BuildShellCommandLine(
            @"C:\Program Files\Angel\AngelBossKey.Next.exe",
            1234);

        Assert.Equal(
            "\"C:\\Program Files\\Angel\\AngelBossKey.Next.exe\" --privacy-shell 1234",
            command);
    }

    [Fact]
    public void ExplorerDesktopShell_QuotesExecutableAndRecognizesRequiredWindows()
    {
        var command = ExplorerDesktopShellHost.BuildCommandLine(@"C:\Windows\explorer.exe");

        Assert.Equal("\"C:\\Windows\\explorer.exe\"", command);
        Assert.True(ExplorerDesktopShellHost.IsRequiredShellClass("Shell_TrayWnd"));
        Assert.True(ExplorerDesktopShellHost.IsRequiredShellClass("Progman"));
        Assert.False(ExplorerDesktopShellHost.IsRequiredShellClass("WorkerW"));
        Assert.True(ExplorerDesktopShellHost.IsExpectedExplorerPath(
            @"C:\Windows\explorer.exe",
            @"c:\windows\EXPLORER.EXE"));
        Assert.False(ExplorerDesktopShellHost.IsExpectedExplorerPath(
            @"C:\Tools\explorer.exe",
            @"C:\Windows\explorer.exe"));
        Assert.True(ExplorerDesktopShellHost.IsToolbarSatisfied(
            showToolbar: false,
            processRunning: false,
            hasVisibleWindow: false));
        Assert.True(ExplorerDesktopShellHost.IsToolbarSatisfied(
            showToolbar: true,
            processRunning: true,
            hasVisibleWindow: true));
        Assert.False(ExplorerDesktopShellHost.IsToolbarSatisfied(
            showToolbar: true,
            processRunning: false,
            hasVisibleWindow: true));
        Assert.False(ExplorerDesktopShellHost.IsToolbarSatisfied(
            showToolbar: true,
            processRunning: true,
            hasVisibleWindow: false));
    }

    [Fact]
    public void ExplorerDesktopToolbar_DisabledPreferenceStopsWithoutStarting()
    {
        var ensureCalls = 0;
        var stopCalls = 0;
        (bool Success, string Error) EnsureToolbar()
        {
            ensureCalls++;
            return (true, string.Empty);
        }
        void StopToolbar() => stopCalls++;

        var enabled = ExplorerDesktopShellHost.ConfigureToolbar(
            showToolbar: true,
            EnsureToolbar,
            StopToolbar);
        var disabled = ExplorerDesktopShellHost.ConfigureToolbar(
            showToolbar: false,
            EnsureToolbar,
            StopToolbar);

        Assert.True(enabled.Success);
        Assert.True(disabled.Success);
        Assert.Equal(1, ensureCalls);
        Assert.Equal(1, stopCalls);
    }

    [Fact]
    public void PrivacyDesktopShell_FullExplorerKeepsExplorerWhenReady()
    {
        var events = new List<string>();

        var result = PrivacyDesktopShellCoordinator.Prepare(
            PrivacyDesktopShellMode.FullExplorer,
            () =>
            {
                events.Add("explorer");
                return (true, "explorer-ready");
            },
            () =>
            {
                events.Add("compatible");
                return (true, "compatible-ready");
            },
            () => events.Add("stop-explorer"),
            () => events.Add("stop-compatible"));

        Assert.True(result.Success);
        Assert.Equal(PrivacyDesktopShellMode.FullExplorer, result.Mode);
        Assert.False(result.UsedFallback);
        Assert.Equal(["stop-compatible", "explorer"], events);
    }

    [Fact]
    public void PrivacyDesktopShell_FallsBackToCompatibilityWhenExplorerFails()
    {
        var events = new List<string>();

        var result = PrivacyDesktopShellCoordinator.Prepare(
            PrivacyDesktopShellMode.FullExplorer,
            () =>
            {
                events.Add("explorer");
                return (false, "explorer-failed");
            },
            () =>
            {
                events.Add("compatible");
                return (true, "compatible-ready");
            },
            () => events.Add("stop-explorer"),
            () => events.Add("stop-compatible"));

        Assert.True(result.Success);
        Assert.Equal(PrivacyDesktopShellMode.Compatibility, result.Mode);
        Assert.True(result.UsedFallback);
        Assert.Contains("explorer-failed", result.Message);
        Assert.Equal(["stop-compatible", "explorer", "compatible"], events);
    }

    [Fact]
    public void PrivacyDesktopShell_CompatibilityDoesNotStartExplorer()
    {
        var explorerCalls = 0;
        var compatibleCalls = 0;
        var stopExplorerCalls = 0;

        var result = PrivacyDesktopShellCoordinator.Prepare(
            PrivacyDesktopShellMode.Compatibility,
            () =>
            {
                explorerCalls++;
                return (true, "unexpected");
            },
            () =>
            {
                compatibleCalls++;
                return (true, "compatible-ready");
            },
            () => stopExplorerCalls++,
            () => { });

        Assert.True(result.Success);
        Assert.Equal(PrivacyDesktopShellMode.Compatibility, result.Mode);
        Assert.False(result.UsedFallback);
        Assert.Equal(0, explorerCalls);
        Assert.Equal(1, compatibleCalls);
        Assert.Equal(1, stopExplorerCalls);
    }

    [Fact]
    public void DesktopJobProcess_BuildsDirectCommandLineWithoutShell()
    {
        Assert.Equal(
            "\"C:\\Program Files\\Editor\\editor.exe\" --profile work",
            DesktopJobProcess.BuildCommandLine(
                @"C:\Program Files\Editor\editor.exe",
                "  --profile work  "));
    }

    [Fact]
    public async Task DesktopShell_RestartsAfterUnexpectedExitWithoutSwitchingDesktop()
    {
        var desktopName = $"AngelBossKey.Next.Test.{Guid.NewGuid():N}";
        var access = NativeMethods.DesktopReadObjects |
            NativeMethods.DesktopCreateWindow |
            NativeMethods.DesktopWriteObjects |
            NativeMethods.DesktopSwitchDesktop;
        var desktop = NativeMethods.CreateDesktop(desktopName, 0, 0, 0, access, 0);
        Assert.NotEqual(0, desktop);

        try
        {
            using var shell = new DesktopShellHost(
                desktop,
                desktopName,
                NativeMethods.GetCurrentThreadId(),
                applicationPath: Path.Combine(AppContext.BaseDirectory, "AngelBossKey.Next.exe"));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Exited += (_, _) => exited.TrySetResult();

            var result = shell.EnsureReady(Guid.NewGuid(), CancellationToken.None);

            Assert.True(result.Success, result.Message);
            using (var process = Process.GetProcessById((int)shell.ProcessId))
            {
                process.Kill();
            }
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var restarted = shell.EnsureReady(Guid.NewGuid(), CancellationToken.None);

            Assert.True(restarted.Success, restarted.Message);
            Assert.NotEqual(0u, shell.ProcessId);
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }

    [Fact]
    public async Task DesktopShell_CancelledRestartPreservesExistingWorkspaceJob()
    {
        var desktopName = $"AngelBossKey.Next.Test.{Guid.NewGuid():N}";
        var access = NativeMethods.DesktopReadObjects |
            NativeMethods.DesktopCreateWindow |
            NativeMethods.DesktopWriteObjects |
            NativeMethods.DesktopSwitchDesktop;
        var desktop = NativeMethods.CreateDesktop(desktopName, 0, 0, 0, access, 0);
        Assert.NotEqual(0, desktop);

        try
        {
            using var shell = new DesktopShellHost(
                desktop,
                desktopName,
                NativeMethods.GetCurrentThreadId(),
                applicationPath: Path.Combine(AppContext.BaseDirectory, "AngelBossKey.Next.exe"));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Exited += (_, _) => exited.TrySetResult();
            var started = shell.EnsureReady(Guid.NewGuid(), CancellationToken.None);
            Assert.True(started.Success, started.Message);
            using (var process = Process.GetProcessById((int)shell.ProcessId)) process.Kill();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                shell.EnsureReady(Guid.NewGuid(), cancellation.Token));
            Assert.True(shell.HasWorkspace);
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }

    [Fact]
    public async Task DesktopShell_ReturnHandshakeClosesOnlyAfterAcknowledgement()
    {
        var receiverReady = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new Thread(() =>
        {
            var probe = new NativeMethods.Message();
            NativeMethods.PeekMessageW(ref probe, 0, 0, 0, NativeMethods.PmNoRemove);
            receiverReady.TrySetResult(NativeMethods.GetCurrentThreadId());
            var message = new NativeMethods.Message();
            if (NativeMethods.GetMessageW(ref message, 0, 0, 0) > 0 &&
                message.Id == PrivacyDesktopShellBridge.ReturnRequestMessage)
            {
                NativeMethods.PostMessageW(
                    (nint)message.WParam,
                    PrivacyDesktopShellBridge.ReturnSucceededMessage,
                    0,
                    0);
            }
        })
        { IsBackground = true };
        receiver.Start();
        var ownerThreadId = await receiverReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var desktopName = $"AngelBossKey.Next.Test.{Guid.NewGuid():N}";
        var access = NativeMethods.DesktopReadObjects |
            NativeMethods.DesktopCreateWindow |
            NativeMethods.DesktopWriteObjects |
            NativeMethods.DesktopSwitchDesktop;
        var desktop = NativeMethods.CreateDesktop(desktopName, 0, 0, 0, access, 0);
        Assert.NotEqual(0, desktop);

        try
        {
            using var shell = new DesktopShellHost(
                desktop,
                desktopName,
                ownerThreadId,
                applicationPath: Path.Combine(AppContext.BaseDirectory, "AngelBossKey.Next.exe"));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Exited += (_, _) => exited.TrySetResult();
            var started = shell.EnsureReady(Guid.NewGuid(), CancellationToken.None);
            Assert.True(started.Success, started.Message);

            Assert.True(PrivacyDesktopShellBridge.RequestReturn(ownerThreadId, shell.ShellWindow));

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(receiver.Join(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }
}
