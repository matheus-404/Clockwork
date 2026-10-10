using Avalonia;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            _desktop = desktop;

            var vm = new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = vm };
            _mainWindow = mainWindow;
            desktop.MainWindow = mainWindow;

            _startupCts = new CancellationTokenSource();
            _presentMonUpdater = new PresentMonUpdateService();
            _appUpdater = new AppUpdateService();

            mainWindow.Opened += (_, _) =>
            {
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

            if (_presentMonUpdater is null)
                return;

            var result = await _presentMonUpdater.EnsureLatestAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (mainWindow.IsVisible)
                {
                    switch (result.Status)
                    {
                        case PresentMonStartupStatus.Installed:
                            await StartupMessageBox.ShowAsync(
                                mainWindow,
                                "PresentMon Required",
                                "PresentMon is required by Clockwork for performance monitoring. " +
                                $"The latest official release ({FormatVersion(result.InstalledVersion)}) has been installed automatically.");
                            break;

                        case PresentMonStartupStatus.Updated:
                            await StartupMessageBox.ShowAsync(
                                mainWindow,
                                "PresentMon Updated",
                                $"Clockwork found a newer PresentMon release and updated it to {FormatVersion(result.LatestVersion)}.");
                            break;

                        case PresentMonStartupStatus.OfflineUsingExisting:
                            await StartupMessageBox.ShowAsync(
                                mainWindow,
                                "PresentMon Update Check",
                                "No internet connection is available, so Clockwork could not check for a newer PresentMon release. " +
                                $"Clockwork will continue using the installed version ({FormatVersion(result.InstalledVersion)}).");
                            break;

                        case PresentMonStartupStatus.MissingOffline:
                            await StartupMessageBox.ShowAsync(
                                mainWindow,
                                "PresentMon Required",
                                "PresentMon is required by Clockwork for performance monitoring, but it is not installed. " +
                                "No internet connection is available, so Clockwork could not download it. " +
                                "Connect to the internet and restart Clockwork.");
                            break;

                        case PresentMonStartupStatus.FailedUsingExisting:
                            await StartupMessageBox.ShowAsync(
                                mainWindow,
                                "PresentMon Update Check",
                                $"Clockwork could not complete the PresentMon update check. " +
                                $"The installed version ({FormatVersion(result.InstalledVersion)}) will be used.\n\n" +
                                result.Message);
                            break;

                        case PresentMonStartupStatus.MissingInstallFailed:
                            await StartupMessageBox.ShowAsync(
                                mainWindow,
                                "PresentMon Required",
                                "PresentMon is required by Clockwork for performance monitoring, but Clockwork could not install it. " +
                                "Monitoring may be unavailable until PresentMon is installed.\n\n" +
                                result.Message);
                            break;
                    }
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
            System.Diagnostics.Debug.WriteLine($"[Clockwork] Startup initialization failed: {ex}");

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!cancellationToken.IsCancellationRequested && mainWindow.IsVisible)
                {
                    await StartupMessageBox.ShowAsync(
                        mainWindow,
                        "Clockwork Startup",
                        "Clockwork could not complete its PresentMon startup check. The application will continue, " +
                        "but performance monitoring may be unavailable until PresentMon is installed.\n\n" +
                        ex.Message);
                }
            });
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
            System.Diagnostics.Debug.WriteLine($"[Clockwork] Application update check failed: {ex}");
        }
    }

    private static string FormatVersion(Version? version) =>
        version is null ? "unknown" : version.ToString(3);
}
