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
