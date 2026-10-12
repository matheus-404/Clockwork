using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Clockwork.Overlay;
using Clockwork.Services;
using Clockwork.ViewModels;
using Clockwork.Views;

namespace Clockwork;

public partial class App : Application
{
    private OverlayController? _overlay;
    private PresentMonUpdateService? _presentMonUpdater;
    private AppUpdateService? _appUpdater;
    private CancellationTokenSource? _startupCts;
    private MainWindow? _mainWindow;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SettingsService? _settingsService;

    private int _isInitialized;
    private int _hasAppliedInitialHide;
    private bool _firstTimeCloseHintShown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            _desktop = desktop;

            _settingsService = new SettingsService();
            var settings = _settingsService.Load();
            _firstTimeCloseHintShown = settings.FirstTimeCloseNoticeShown;

            var startMinimized = (desktop.Args?.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase)) ?? false)
                || settings.StartMinimized;

            var vm = new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = vm };
            _mainWindow = mainWindow;
            desktop.MainWindow = mainWindow;

            if (startMinimized)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
            }

            mainWindow.Closing += async (sender, e) =>
            {
                if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                    return;

                if (!_firstTimeCloseHintShown)
                {
                    _firstTimeCloseHintShown = true;
                    SaveFirstTimeHintSeen(vm);

                    await StartupMessageBox.ShowAsync(
                        mainWindow,
                        "Running in Background",
                        "Clockwork has minimized to your system tray to monitor games continuously.\n\n" +
                        "Click or right-click the Clockwork tray icon to open settings or exit the app.");
                }
            };

            _startupCts = new CancellationTokenSource();
            _presentMonUpdater = new PresentMonUpdateService();
            _appUpdater = new AppUpdateService();

            mainWindow.Opened += (_, _) =>
            {
                if (startMinimized && Interlocked.CompareExchange(ref _hasAppliedInitialHide, 1, 0) == 0)
                {
                    mainWindow.ShowInTaskbar = false;
                    mainWindow.Hide();
                }

                if (Interlocked.CompareExchange(ref _isInitialized, 1, 0) == 0)
                {
                    // The overlay starts right away. It copes with PresentMon being missing (it
                    // retries with a backoff), so a slow download, an unanswered administrator
                    // prompt or an open message box can never delay or block it.
                    _overlay = new OverlayController(vm);

                    _ = InitializeClockworkAsync(mainWindow, _startupCts.Token);
                }
            };

            desktop.Exit += (_, _) =>
            {
                _startupCts?.Cancel();

                vm.FlushPendingSave();

                _overlay?.Dispose();
                _presentMonUpdater?.Dispose();
                _appUpdater?.Dispose();

                _startupCts?.Dispose();
                _startupCts = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) =>
        ShowMainWindow();

    private void ShowWindow_OnClick(object? sender, EventArgs e) =>
        ShowMainWindow();

    private void ExitClockwork_OnClick(object? sender, EventArgs e) =>
        _desktop?.Shutdown();

    public void ShowMainWindow()
    {
        _mainWindow?.RestoreFromTray();
    }

    private async Task InitializeClockworkAsync(
        MainWindow mainWindow,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = CheckForAppUpdateAsync(cancellationToken);
            _appUpdater?.StartPeriodicChecks(TimeSpan.FromHours(24), cancellationToken);

            if (_presentMonUpdater is null)
                return;

            var result = await _presentMonUpdater.EnsureLatestAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                switch (result.Status)
                {
                    case PresentMonStartupStatus.Installed when mainWindow.IsVisible:
                        await StartupMessageBox.ShowAsync(
                            mainWindow,
                            "PresentMon Required",
                            "PresentMon is required by Clockwork for performance monitoring. " +
                            $"The official release ({FormatVersion(result.InstalledVersion)}) has been installed automatically.");
                        break;

                    case PresentMonStartupStatus.Updated when mainWindow.IsVisible:
                        await StartupMessageBox.ShowAsync(
                            mainWindow,
                            "PresentMon Updated",
                            $"Clockwork found a newer PresentMon release and updated it to {FormatVersion(result.LatestVersion)}.");
                        break;

                    case PresentMonStartupStatus.OfflineUsingExisting:
                        // Benign offline state: suppress popup modal and continue using installed version silently
                        TelemetryDiagnostics.Write($"PresentMon update check offline; continuing with installed version {FormatVersion(result.InstalledVersion)}.");
                        break;

                    case PresentMonStartupStatus.MissingOffline when mainWindow.IsVisible:
                        await StartupMessageBox.ShowAsync(
                            mainWindow,
                            "PresentMon Required",
                            "PresentMon is required by Clockwork for performance monitoring, but it is not installed. " +
                            "No internet connection is available, so Clockwork could not download it. " +
                            "Connect to the internet and restart Clockwork.");
                        break;

                    case PresentMonStartupStatus.FailedUsingExisting:
                        // Benign failure (e.g. rate-limit or network timeout): suppress popup modal when an existing compatible version is running
                        TelemetryDiagnostics.Write($"PresentMon update check skipped ({result.Message}); continuing with installed version {FormatVersion(result.InstalledVersion)}.");
                        break;

                    case PresentMonStartupStatus.MissingInstallFailed when mainWindow.IsVisible:
                        await StartupMessageBox.ShowAsync(
                            mainWindow,
                            "PresentMon Required",
                            "PresentMon is required by Clockwork for performance monitoring, but Clockwork could not install it. " +
                            "Monitoring may be unavailable until PresentMon is installed.\n\n" +
                            result.Message);
                        break;
                }

                // PresentMon may have just been installed or updated: let the overlay reconnect now.
                if (!cancellationToken.IsCancellationRequested &&
                    result.Status is PresentMonStartupStatus.Installed or PresentMonStartupStatus.Updated)
                    _overlay?.NotifyPresentMonChanged();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TelemetryDiagnostics.Write($"Startup initialization error: {ex.Message}");
        }
    }

    private async Task CheckForAppUpdateAsync(CancellationToken cancellationToken)
    {
        if (_appUpdater is null)
            return;

        try
        {
            await _appUpdater.CheckDownloadAndScheduleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TelemetryDiagnostics.Write($"Application update check failed: {ex.Message}");
        }
    }

    private void SaveFirstTimeHintSeen(MainWindowViewModel vm)
    {
        try
        {
            _settingsService?.Save(
                vm.Sections.SelectMany(s => s.Options.Select(o => (o.Stat.Key, o.IsOn))),
                vm.OverlayPositionX,
                vm.OverlayPositionY,
                vm.OverlayScale,
                vm.OverlayBackgroundEnabled,
                vm.OverlayBackgroundOpacity,
                vm.IncludeWindowedGames,
                firstTimeCloseNoticeShown: true);
        }
        catch
        {
        }
    }

    private static string FormatVersion(Version? version) =>
        version is null ? "unknown" : version.ToString(3);
}