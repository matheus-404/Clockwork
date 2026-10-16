using System.Runtime;
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

    private int _isInitialized;
    private int _hasAppliedInitialHide;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    internal void ToggleOverlay() => _overlay?.ToggleVisibility();

    internal static void TrimMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
        }
        catch
        {
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _desktop = desktop;

            var cliMinimized = desktop.Args?.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase)) ?? false;

            // The view model is the only component that writes settings.json. It loads the
            // saved state (including the first-close notice flag) and saves every value together.
            var vm = new MainWindowViewModel();
            var startMinimized = cliMinimized || vm.StartMinimized;

            var mainWindow = new MainWindow { DataContext = vm };
            _mainWindow = mainWindow;
            desktop.MainWindow = mainWindow;

            if (startMinimized)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
            }

            mainWindow.Closing += (_, e) =>
            {
                if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                    return;

                if (vm.FirstTimeCloseNoticeShown)
                    return;

                // Updates the view model's own state and schedules the normal debounced save,
                // so no other setting is reset and later saves keep the flag set.
                vm.FirstTimeCloseNoticeShown = true;

                // MainWindow.OnClosing hides the window right after this event returns, so the
                // notice is posted until then. It opens as a standalone dialog instead of being
                // owned by a window that is about to disappear.
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await StartupMessageBox.ShowAsync(
                            null,
                            "Running in Background",
                            "Clockwork has minimized to your system tray to monitor games continuously.\n\n" +
                            "Click or right-click the Clockwork tray icon to open settings or exit the app.");
                    }
                    catch (Exception ex)
                    {
                        TelemetryDiagnostics.Write($"Background notice failed: {ex.Message}");
                    }
                });
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
                    TrimMemory();
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
                Interlocked.Exchange(ref _presentMonUpdater, null)?.Dispose();
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

            var updater = Interlocked.Exchange(ref _presentMonUpdater, null);
            if (updater is null)
                return;

            PresentMonStartupResult result;
            try
            {
                result = await updater.EnsureLatestAsync(cancellationToken);
            }
            finally
            {
                updater.Dispose();
            }

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

            if (!mainWindow.IsVisible)
                TrimMemory();
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

    private static string FormatVersion(Version? version) =>
        version is null ? "unknown" : version.ToString(3);
}