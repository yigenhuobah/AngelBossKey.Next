using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.App.Services;
using AngelBossKey.Next.App.ViewModels;
using AngelBossKey.Next.Core.Abstractions;
using AngelBossKey.Next.Core.Storage;
using AngelBossKey.Next.Win32;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace AngelBossKey.Next.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private GlobalHotkeyService? _hotkeyService;
    private WindowEventWatcher? _windowEventWatcher;
    private TrayIconService? _trayIcon;
    private IWindowVisibilityController? _visibilityController;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private HwndSource? _windowSource;
    private bool _isExiting;
    private bool _servicesDisposed;
    private bool _activationPending;

    public static App Instance => (App)Current;
    public bool IsExiting => _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        var settingsStore = new JsonSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var recoveryStore = new JsonRecoveryStore(Path.Combine(dataDirectory, "recovery.json"));
        var settings = await settingsStore.LoadAsync();

        var windowCatalog = new WindowCatalog();
        _visibilityController = new WindowVisibilityController(windowCatalog, recoveryStore);
        var startupRegistration = new StartupRegistration();
        _hotkeyService = new GlobalHotkeyService();
        _viewModel = new MainWindowViewModel(
            settings,
            settingsStore,
            _visibilityController,
            startupRegistration,
            _hotkeyService);

        _mainWindow = new MainWindow(_viewModel, windowCatalog);
        MainWindow = _mainWindow;

        var handle = new WindowInteropHelper(_mainWindow).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource.AddHook(WindowProcedure);
        _hotkeyService.AttachWindow(handle);
        _hotkeyService.Pressed += async (_, _) => await Dispatcher.InvokeAsync(ToggleVisibilityAsync);
        await _viewModel.InitializeHotkeyAsync();

        var recovered = await _visibilityController.RecoverAsync();
        _viewModel.SetRecoveryResult(recovered);

        _windowEventWatcher = new WindowEventWatcher(_visibilityController);
        _windowEventWatcher.Start();

        _trayIcon = new TrayIconService(
            () => Dispatcher.Invoke(ShowMainWindow),
            () => Dispatcher.InvokeAsync(ToggleVisibilityAsync),
            () => Dispatcher.InvokeAsync(RequestExitAsync));
        _visibilityController.StateChanged += (_, _) =>
            Dispatcher.Invoke(() => _trayIcon?.Update(_visibilityController.IsHidden));

        var background = e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (_activationPending || !background || !settings.Hotkey.IsConfigured)
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

        _isExiting = true;
        try
        {
            if (_visibilityController is not null)
            {
                await _visibilityController.RestoreAsync();
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
        // Session shutdown must not block the UI thread. Any window still hidden is
        // covered by the recovery journal and restored on the next launch.
        DisposeServices();

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

    private nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        _hotkeyService?.HandleMessage(message, wParam);
        return 0;
    }

    private void DisposeServices()
    {
        if (_servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;
        _trayIcon?.Dispose();
        _windowEventWatcher?.Dispose();
        _hotkeyService?.Dispose();
        _windowSource?.RemoveHook(WindowProcedure);
        _singleInstance?.Dispose();
    }
}
