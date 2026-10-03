using System;
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
    /// Checks for an update and downloads it in the background. The update is installed when
    /// Clockwork is closed; it never restarts the app mid-session. Returns true if an update
    /// was downloaded and scheduled.
    /// </summary>
    public async Task<bool> CheckDownloadAndScheduleAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            return false;

        await _updateLock.WaitAsync(cancellationToken);

        try
        {
            var update =
                await _updateManager.CheckForUpdatesAsync();

            if (update is null)
                return false;

            await _updateManager.DownloadUpdatesAsync(
                update,
                cancelToken: cancellationToken);

            // Install silently after Clockwork exits, without relaunching it. Velopack waits for
            // this process to end, so the overlay is never interrupted while a game is running.
            _updateManager.WaitExitThenApplyUpdates(
                update.TargetFullRelease,
                silent: true,
                restart: false);

            return true;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public void Dispose()
    {
        _updateLock.Dispose();
    }
}