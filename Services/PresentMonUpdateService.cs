using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clockwork.Services;

/// <summary>
/// Keeps the separately-installed PresentMon dependency current without embedding a
/// PresentMon version in Clockwork's source code.
/// </summary>
public sealed class PresentMonUpdateService : IDisposable
{
    private const string GitHubLatestReleaseUrl =
        "https://api.github.com/repos/GameTechDev/PresentMon/releases/latest";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;

    public PresentMonUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout,
        };

        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Clockwork", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<PresentMonStartupResult> EnsureLatestAsync(
        CancellationToken cancellationToken = default)
    {
        var installed = GetInstalledVersion();
        var installedAvailable = installed is not null;

        PresentMonRelease release;
        try
        {
            release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return installedAvailable
                ? PresentMonStartupResult.Offline(installed)
                : PresentMonStartupResult.MissingOffline();
        }
        catch (PresentMonUpdateCheckException ex)
        {
            return ex.IsOffline
                ? installedAvailable
                    ? PresentMonStartupResult.Offline(installed)
                    : PresentMonStartupResult.MissingOffline()
                : installedAvailable
                    ? PresentMonStartupResult.Failed(installed, ex.Message)
                    : PresentMonStartupResult.MissingFailed(ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            return installedAvailable
                ? PresentMonStartupResult.Offline(installed)
                : PresentMonStartupResult.MissingOffline();
        }
        catch (HttpRequestException ex)
        {
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, $"PresentMon update check failed: {ex.Message}")
                : PresentMonStartupResult.MissingFailed($"PresentMon update check failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, $"PresentMon update check failed: {ex.Message}")
                : PresentMonStartupResult.MissingFailed($"PresentMon update check failed: {ex.Message}");
        }

        if (!Version.TryParse(NormalizeVersion(release.TagName), out var latestVersion))
        {
            var message = $"The latest PresentMon release tag '{release.TagName}' is not a valid version.";
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, message)
                : PresentMonStartupResult.MissingFailed(message);
        }

        if (installed is not null && installed >= latestVersion)
            return PresentMonStartupResult.Current(installed, latestVersion);

        var msi = SelectMsi(release);
        if (msi is null)
        {
            var message = "The latest PresentMon release did not contain an MSI installer.";
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, message)
                : PresentMonStartupResult.MissingFailed(message);
        }

        var temporaryMsi = Path.Combine(
            Path.GetTempPath(),
            $"Clockwork-PresentMon-{Guid.NewGuid():N}.msi");

        try
        {
            await DownloadMsiAsync(msi.BrowserDownloadUrl, temporaryMsi, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(msi.Digest) &&
                !await VerifySha256Async(temporaryMsi, msi.Digest!, cancellationToken).ConfigureAwait(false))
            {
                return installedAvailable
                    ? PresentMonStartupResult.Failed(installed, "The downloaded PresentMon MSI failed its SHA-256 integrity check.")
                    : PresentMonStartupResult.MissingFailed("The downloaded PresentMon MSI failed its SHA-256 integrity check.");
            }

            var installResult = await InstallMsiAsync(temporaryMsi, cancellationToken)
                .ConfigureAwait(false);

            if (!installResult.Success)
            {
                return installedAvailable
                    ? PresentMonStartupResult.Failed(installed, installResult.Message)
                    : PresentMonStartupResult.MissingFailed(installResult.Message);
            }

            var updated = GetInstalledVersion();
            if (updated is null || updated < latestVersion)
            {
                var message =
                    $"PresentMon installation completed, but Clockwork could not verify version {latestVersion}.";
                return installedAvailable
                    ? PresentMonStartupResult.Failed(installed, message)
                    : PresentMonStartupResult.MissingFailed(message);
            }

            return installed is null
                ? PresentMonStartupResult.Installed(updated)
                : PresentMonStartupResult.Updated(updated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, "The PresentMon download timed out.")
                : PresentMonStartupResult.MissingFailed("The PresentMon download timed out.");
        }
        catch (HttpRequestException ex)
        {
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, $"PresentMon could not be downloaded: {ex.Message}")
                : PresentMonStartupResult.MissingFailed($"PresentMon could not be downloaded: {ex.Message}");
        }
        catch (Exception ex)
        {
            return installedAvailable
                ? PresentMonStartupResult.Failed(installed, $"PresentMon installation failed: {ex.Message}")
                : PresentMonStartupResult.MissingFailed($"PresentMon installation failed: {ex.Message}");
        }
        finally
        {
            TryDelete(temporaryMsi);
        }
    }

    private async Task<PresentMonRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            GitHubLatestReleaseUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new PresentMonUpdateCheckException(
                false,
                "GitHub did not allow the update check (the API may be rate-limited).");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new PresentMonUpdateCheckException(
                false,
                $"GitHub returned server error {(int)response.StatusCode} while checking for PresentMon updates.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var release = await JsonSerializer.DeserializeAsync<PresentMonRelease>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return release ?? throw new InvalidOperationException("GitHub returned an empty release response.");
    }

    private async Task DownloadMsiAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PresentMon provided an invalid HTTPS GitHub download URL.");
        }

        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifySha256Async(
        string path,
        string digest,
        CancellationToken cancellationToken)
    {
        var expected = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..]
            : digest;

        if (expected.Length != 64)
            return false;

        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expected));
    }

    private static async Task<MsiInstallResult> InstallMsiAsync(
        string msiPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(msiPath);
        startInfo.ArgumentList.Add("/qn");
        startInfo.ArgumentList.Add("/norestart");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Installer could not be started.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode is 0 or 3010
            ? new MsiInstallResult(true, string.Empty)
            : new MsiInstallResult(
                false,
                $"Windows Installer exited with code {process.ExitCode}.");
    }

    private static Version? GetInstalledVersion()
    {
        if (!PresentMonNative.Api.TryLoad(out var api, out _)
            || api is null)
        {
            return null;
        }

        try
        {
            var fileVersion = FileVersionInfo.GetVersionInfo(api.LoadedPath).FileVersion;
            if (Version.TryParse(NormalizeVersion(fileVersion), out var version))
                return NormalizeToReleaseVersion(version);

            return null;
        }
        finally
        {
            api.Dispose();
        }
    }

    private static PresentMonAsset? SelectMsi(PresentMonRelease release)
    {
        var msis = release.Assets
            .Where(static asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (msis.Length == 0)
            return null;

        return msis.FirstOrDefault(static asset =>
                   asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
               ?? msis.First();
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        if (normalized.StartsWith('v'))
            normalized = normalized[1..];

        var separator = normalized.IndexOfAny(['-', '+']);
        return separator >= 0 ? normalized[..separator] : normalized;
    }

    private static Version NormalizeToReleaseVersion(Version version) =>
        new(
            version.Major,
            version.Minor,
            version.Build < 0 ? 0 : version.Build);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    public void Dispose() => _httpClient.Dispose();


    private sealed class PresentMonUpdateCheckException : Exception
    {
        internal bool IsOffline { get; }

        internal PresentMonUpdateCheckException(bool isOffline, string message)
            : base(message)
        {
            IsOffline = isOffline;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class PresentMonRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<PresentMonAsset> Assets { get; init; } = [];
    }

    private sealed class PresentMonAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }

    private readonly record struct MsiInstallResult(bool Success, string Message);
}

public enum PresentMonStartupStatus
{
    Current,
    Installed,
    Updated,
    OfflineUsingExisting,
    MissingOffline,
    FailedUsingExisting,
    MissingInstallFailed,
}

public sealed record PresentMonStartupResult(
    PresentMonStartupStatus Status,
    Version? InstalledVersion,
    Version? LatestVersion,
    string? Message)
{
    public static PresentMonStartupResult Current(Version installed, Version latest) =>
        new(PresentMonStartupStatus.Current, installed, latest, null);

    public static PresentMonStartupResult Installed(Version installed) =>
        new(PresentMonStartupStatus.Installed, installed, installed, null);

    public static PresentMonStartupResult Updated(Version updated) =>
        new(PresentMonStartupStatus.Updated, updated, updated, null);

    public static PresentMonStartupResult Offline(Version? installed) =>
        installed is null
            ? MissingOffline()
            : new(PresentMonStartupStatus.OfflineUsingExisting, installed, null, null);

    public static PresentMonStartupResult MissingOffline() =>
        new(PresentMonStartupStatus.MissingOffline, null, null, null);

    public static PresentMonStartupResult Failed(Version? installed, string message) =>
        installed is null
            ? MissingFailed(message)
            : new(PresentMonStartupStatus.FailedUsingExisting, installed, null, message);

    public static PresentMonStartupResult MissingFailed(string message) =>
        new(PresentMonStartupStatus.MissingInstallFailed, null, null, message);
}
