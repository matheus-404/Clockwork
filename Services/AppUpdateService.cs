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

    public async Task<bool> CheckDownloadAndApplyAsync(
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

            _updateManager.ApplyUpdatesAndRestart(update);

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