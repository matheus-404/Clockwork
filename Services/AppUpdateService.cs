using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Clockwork.Services;

public sealed class AppUpdateService : IDisposable
{
    private const string GitHubRepoUrl =
        "https://github.com/matheus-404/Clockwork";

    private readonly UpdateManager _updateManager;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private CancellationTokenSource? _periodicCts;
    private Task? _periodicTask;
    private bool _disposed;

    public AppUpdateService()
    {
        _updateManager = new UpdateManager(
            new GithubSource(
                GitHubRepoUrl,
                accessToken: null,
                prerelease: false));
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    /// <summary>
    /// Starts a background periodic timer to check for updates while the application
    /// runs continuously in the tray.
    /// </summary>
    public void StartPeriodicChecks(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled || _disposed)
            return;

        _periodicCts?.Cancel();
        _periodicCts?.Dispose();

        _periodicCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _periodicCts.Token;

        _periodicTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    try
                    {
                        await CheckDownloadAndScheduleAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        TelemetryDiagnostics.Write($"Periodic update check failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    /// <summary>
    /// Checks for an update and downloads it in the background. The update is installed when
    /// Clockwork is closed; it never restarts the app mid-session. Returns true if an update
    /// was downloaded and scheduled.
    /// </summary>
    public async Task<bool> CheckDownloadAndScheduleAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsInstalled || _disposed)
            return false;

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var update = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
                return false;

            TelemetryDiagnostics.Write($"Found Clockwork update: {update.TargetFullRelease.Version}. Downloading in background...");

            await _updateManager.DownloadUpdatesAsync(
                update,
                cancelToken: cancellationToken).ConfigureAwait(false);

            TelemetryDiagnostics.Write($"Downloaded Clockwork update: {update.TargetFullRelease.Version}. Scheduled on exit.");

            // Install silently after Clockwork exits, without relaunching it. Velopack waits for
            // this process to end, so the overlay is never interrupted while a game is running.
            _updateManager.WaitExitThenApplyUpdates(
                update.TargetFullRelease,
                silent: true,
                restart: false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TelemetryDiagnostics.Write($"App update check/download skipped: {ex.Message}");
            return false;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _periodicCts?.Cancel();
        _periodicCts?.Dispose();
        _updateLock.Dispose();
    }
}