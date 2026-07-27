using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.App.Services;
using AngelBossKey.Next.App.ViewModels;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Models;
using AngelBossKey.Next.Core.Storage;
using AngelBossKey.Next.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AngelBossKey.Next.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private GlobalHotkeyService? _hotkeyService;
    private WindowEventWatcher? _windowEventWatcher;
    private TrayIconService? _trayIcon;
    private WindowStateMonitor? _windowStateMonitor;
    private IApplicationAudioController? _audioController;
    private IAutomationTriggerService? _automationService;
    private IPrivacyDesktopService? _privacyDesktop;
    private IDiagnosticLog? _diagnosticLog;
    private IWindowVisibilityController? _visibilityController;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private HwndSource? _windowSource;
    private bool _isExiting;
    private bool _servicesDisposed;
    private bool _activationPending;
    private int _trayStateRefreshQueued;
    private uint _taskbarCreatedMessage;

    public static App Instance => (App)Current;
    public bool IsExiting => _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var privacyShellIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--privacy-shell", StringComparison.OrdinalIgnoreCase));
        if (privacyShellIndex >= 0 && e.Args.Length > privacyShellIndex + 1 &&
            uint.TryParse(e.Args[privacyShellIndex + 1], out var ownerThreadId))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var sceneId = e.Args.Length > privacyShellIndex + 2 &&
                Guid.TryParse(e.Args[privacyShellIndex + 2], out var parsedSceneId)
                ? parsedSceneId
                : Guid.Empty;
            var shell = new PrivacyShellWindow(ownerThreadId, sceneId);
            MainWindow = shell;
            shell.Show();
            return;
        }

        var privacyToolbarIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--privacy-toolbar", StringComparison.OrdinalIgnoreCase));
        if (privacyToolbarIndex >= 0 && e.Args.Length > privacyToolbarIndex + 2 &&
            uint.TryParse(e.Args[privacyToolbarIndex + 1], out var toolbarOwnerThreadId) &&
            Guid.TryParse(e.Args[privacyToolbarIndex + 2], out var toolbarSceneId))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var toolbar = new PrivacyToolbarWindow(toolbarOwnerThreadId, toolbarSceneId);
            MainWindow = toolbar;
            toolbar.Show();
            return;
        }

        var brokerIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--elevated-broker", StringComparison.OrdinalIgnoreCase));
        if (brokerIndex >= 0 && e.Args.Length >= brokerIndex + 3)
        {
            var exitCode = await ElevatedWindowBrokerServer.RunAsync(
                e.Args[brokerIndex + 1],
                e.Args[brokerIndex + 2]);
            Shutdown(exitCode);
            return;
        }

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            await SingleInstanceService.NotifyPrimaryAsync();
            Shutdown();
            return;
        }

        _singleInstance.ActivationRequested += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_mainWindow is null)
            {
                _activationPending = true;
                return;
            }

            ShowMainWindow();
        });
        _singleInstance.StartServer();

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AngelBossKey.Next");
        _diagnosticLog = new RollingDiagnosticLog(Path.Combine(dataDirectory, "logs"));
        var appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        _diagnosticLog.Info("app.start", $"version={appVersion}; background={e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)}");
        var settingsStore = new JsonSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var recoveryStore = new JsonRecoveryStore(Path.Combine(dataDirectory, "recovery.json"));
        var audioRecoveryStore = new JsonAudioRecoveryStore(Path.Combine(dataDirectory, "audio-recovery.json"));
        AppSettings settings;
        try
        {
            settings = await settingsStore.LoadAsync();
            await recoveryStore.LoadAsync();
            await audioRecoveryStore.LoadAsync();
        }
        catch (Exception exception) when (IsPersistentStateReadFailure(exception))
        {
            StopAfterPersistentStateReadFailure(exception);
            return;
        }

        var windowCatalog = new WindowCatalog();
        var elevatedBroker = new ElevatedWindowBrokerClient(
            settings.EnableElevatedBroker,
            Environment.ProcessPath!,
            _diagnosticLog);
        _visibilityController = new WindowVisibilityController(
            windowCatalog,
            recoveryStore,
            _diagnosticLog,
            elevatedBroker);
        _audioController = new ApplicationAudioController(audioRecoveryStore, _diagnosticLog);
        VisibilityOperationResult recovered;
        try
        {
            await _audioController.RecoverAsync();
            recovered = await _visibilityController.RecoverAsync();
        }
        catch (Exception exception) when (IsPersistentStateReadFailure(exception))
        {
            StopAfterPersistentStateReadFailure(exception);
            return;
        }

        _automationService = new AutomationTriggerService(_diagnosticLog);
        _privacyDesktop = new PrivacyDesktopService(_diagnosticLog);
        var startupRegistration = new StartupRegistration();
        _hotkeyService = new GlobalHotkeyService();
        _viewModel = new MainWindowViewModel(
            settings,
            settingsStore,
            _visibilityController,
            startupRegistration,
            _hotkeyService,
            _diagnosticLog,
            _audioController,
            _automationService,
            _privacyDesktop,
            elevatedBroker);

        _mainWindow = new MainWindow(_viewModel, windowCatalog);
        MainWindow = _mainWindow;

        var handle = new WindowInteropHelper(_mainWindow).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource.AddHook(WindowProcedure);
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _hotkeyService.AttachWindow(handle);
        _hotkeyService.RegistrationPressed += async (_, args) =>
            await DispatchOperationAsync(
                () => _viewModel.ActivateSceneAsync(args.RegistrationId),
                "hotkey.dispatch");
        _automationService.Triggered += async (_, args) =>
            await DispatchOperationAsync(
                () => _viewModel.HandleAutomationAsync(args.Source),
                "automation.dispatch");
        await _viewModel.InitializeAsync();
        _viewModel.SetRecoveryResult(recovered);

        _windowEventWatcher = new WindowEventWatcher(_visibilityController, _diagnosticLog);
        _windowEventWatcher.Start();
        _windowStateMonitor = new WindowStateMonitor(_visibilityController, _diagnosticLog);
        _windowStateMonitor.Start();

        _trayIcon = new TrayIconService(
            () => Dispatcher.Invoke(ShowMainWindow),
            () => Dispatcher.InvokeAsync(ToggleVisibilityAsync),
            () => Dispatcher.InvokeAsync(RequestExitAsync),
            sceneId => Dispatcher.InvokeAsync(() =>
                _ = DispatchOperationAsync(
                    () => _viewModel.ActivateSceneAsync(sceneId),
                    "tray.scene")));
        _viewModel.SceneMenuChanged += (_, _) => Dispatcher.Invoke(RefreshTrayScenes);
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.SelectedScene))
            {
                Dispatcher.Invoke(RefreshTrayScenes);
            }
        };
        RefreshTrayScenes();
        _visibilityController.StateChanged += (_, _) => QueueTrayStateRefresh();
        _privacyDesktop.StateChanged += (_, _) => QueueTrayStateRefresh();

        var background = e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (_activationPending || !background || !_viewModel.CanToggle)
        {
            ShowMainWindow();
        }
    }

    public void HandleWindowClose(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        if (_viewModel?.CloseToTray == true)
        {
            _mainWindow?.Hide();
        }
        else
        {
            _ = RequestExitAsync();
        }
    }

    public async Task RequestExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        if (_privacyDesktop?.HasWorkspace == true && _privacyDesktop.RunningApplicationCount > 0)
        {
            var count = _privacyDesktop.RunningApplicationCount;
            var choice = System.Windows.MessageBox.Show(
                $"独立工作区仍有 {count} 个程序。退出会关闭它们，请先保存工作。是否继续退出？",
                "退出天使老板键 Next",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;
        }

        _isExiting = true;
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.RestoreAllAsync();
            }

            if (_viewModel is not null)
            {
                await _viewModel.FlushSettingsAsync();
            }
        }
        finally
        {
            DisposeServices();
            _mainWindow?.Close();
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        // Preserve recovery journals and avoid COM enumeration/thread joins in the
        // limited session-ending window. The process exit releases remaining resources.
        DisposeServices(sessionEnding: true);

        base.OnSessionEnding(e);
    }

    private async Task ToggleVisibilityAsync()
    {
        if (_isExiting || _viewModel is null)
        {
            return;
        }

        await _viewModel.ToggleVisibilityAsync();
        _trayIcon?.Update(_viewModel.IsHidden);
    }

    private async Task DispatchOperationAsync(Func<Task> operation, string eventName)
    {
        try
        {
            await Dispatcher.InvokeAsync(operation).Task.Unwrap();
        }
        catch (Exception exception)
        {
            _diagnosticLog?.LogError(eventName, exception);
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void RefreshTrayScenes()
    {
        if (_trayIcon is null || _viewModel is null) return;
        _trayIcon.UpdateScenes(
            _viewModel.Scenes.Select(scene => new TraySceneEntry(
                scene.Id,
                $"{scene.Name}  [{scene.HotkeyText}]")),
            _viewModel.SelectedScene.Id);
    }

    private void QueueTrayStateRefresh()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _trayStateRefreshQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                // Clear the coalescing flag before reading state. A change that
                // races with this update can then schedule a follow-up refresh.
                Volatile.Write(ref _trayStateRefreshQueued, 0);
                if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                _trayIcon?.Update(
                    _privacyDesktop?.IsActive == true || _visibilityController?.IsHidden == true);
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _trayStateRefreshQueued, 0);
            // The WPF dispatcher is already shutting down.
        }
    }

    private void StopAfterPersistentStateReadFailure(Exception exception)
    {
        _isExiting = true;
        _diagnosticLog?.LogError("app.startup.storage-read", exception);
        try
        {
            System.Windows.MessageBox.Show(
                "无法读取应用配置或恢复数据。为避免覆盖现有文件，天使老板键 Next 未启动。请关闭正在占用这些文件的程序后重试。",
                "天使老板键 Next",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception displayException)
        {
            _diagnosticLog?.LogError("app.startup.storage-read.display", displayException);
        }
        finally
        {
            try
            {
                DisposeServices();
            }
            finally
            {
                Shutdown(-1);
            }
        }
    }

    private static bool IsPersistentStateReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException;

    private nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            _trayIcon?.RefreshAfterExplorerRestart();
            _diagnosticLog?.Info("tray.recreated", "explorer-restart=true");
        }

        _hotkeyService?.HandleMessage(message, wParam);
        return 0;
    }

    private void DisposeServices(bool sessionEnding = false)
    {
        if (_servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;
        _diagnosticLog?.Info("app.stop", $"hidden={_visibilityController?.IsHidden == true}");
        _windowStateMonitor?.Dispose();
        _automationService?.Dispose();
        if (sessionEnding)
        {
            // Session-ending is a synchronous WPF callback. ReturnAsync only performs
            // the bounded desktop switch here; full service disposal is deferred to exit.
            try { _privacyDesktop?.ReturnAsync().GetAwaiter().GetResult(); }
            catch (Exception exception) { _diagnosticLog?.LogError("desktop.session-ending", exception); }
        }
        else
        {
            _privacyDesktop?.Dispose();
            _audioController?.Dispose();
        }
        _trayIcon?.Dispose();
        _windowEventWatcher?.Dispose();
        _hotkeyService?.Dispose();
        _windowSource?.RemoveHook(WindowProcedure);
        _singleInstance?.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);
}
