using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clockwork.Services;

/// <summary>
/// Installs the separately-installed PresentMon dependency when it is missing or too old,
/// without embedding a PresentMon version in Clockwork's source code.
/// </summary>
public sealed class PresentMonUpdateService : IDisposable
{
    private const string GitHubReleasesUrl =
        "https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=30";

    private const int ErrorCancelled = 1223;

    private static readonly Version MinimumSupportedVersion = new(2, 3, 1);
    private const int MaxAutoInstallReleaseMajor = 2;
    private static readonly bool AutoUpdateWorkingInstall = false;
    private static readonly bool RequireSignedMsi = false;
    private const string ExpectedSignerName = "Intel";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;

    public PresentMonUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout,
        };

        var appVersion = typeof(PresentMonUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Clockwork", appVersion));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<PresentMonStartupResult> EnsureLatestAsync(
        CancellationToken cancellationToken = default)
    {
        var installed = GetInstalledVersion();
        var installedAvailable = installed is not null;

        if (installed is not null && installed >= MinimumSupportedVersion && !AutoUpdateWorkingInstall)
            return PresentMonStartupResult.Current(installed, installed);

        List<PresentMonRelease> releases;
        try
        {
            releases = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                : Fail(installed, ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            return installedAvailable
                ? PresentMonStartupResult.Offline(installed)
                : PresentMonStartupResult.MissingOffline();
        }
        catch (Exception ex)
        {
            return Fail(installed, $"PresentMon update check failed: {ex.Message}");
        }

        var selected = SelectRelease(releases);
        if (selected is null)
        {
            return Fail(
                installed,
                $"No PresentMon release (version {MaxAutoInstallReleaseMajor}.x) with an MSI installer was found.");
        }

        var (release, latestVersion) = selected.Value;

        if (installed is not null && installed >= latestVersion)
            return PresentMonStartupResult.Current(installed, latestVersion);

        var msi = SelectMsi(release)!;

        var temporaryMsi = Path.Combine(
            Path.GetTempPath(),
            $"Clockwork-PresentMon-{Guid.NewGuid():N}.msi");

        try
        {
            await DownloadMsiAsync(msi.BrowserDownloadUrl, temporaryMsi, cancellationToken)
                .ConfigureAwait(false);

            var expectedSha = await ResolveExpectedSha256Async(release, msi, cancellationToken)
                .ConfigureAwait(false);

            if (expectedSha is null)
            {
                return Fail(
                    installed,
                    "The release did not provide a SHA-256 checksum for the PresentMon installer, " +
                    "so it was not installed.");
            }

            var failure = await VerifyAndInstallAsync(temporaryMsi, expectedSha, cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
                return Fail(installed, failure);

            var updated = GetInstalledVersion();
            if (updated is null || updated < latestVersion)
            {
                return Fail(
                    installed,
                    $"PresentMon installation completed, but Clockwork could not verify version {latestVersion}.");
            }

            return installed is null
                ? PresentMonStartupResult.Installed(updated)
                : PresentMonStartupResult.Updated(updated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(installed, "The PresentMon download timed out.");
        }
        catch (HttpRequestException ex)
        {
            return Fail(installed, $"PresentMon could not be downloaded: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail(installed, $"PresentMon installation failed: {ex.Message}");
        }
        finally
        {
            TryDelete(temporaryMsi);
        }
    }

    private static async Task<string?> VerifyAndInstallAsync(
        string msiPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(
            msiPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (!await VerifySha256Async(stream, expectedSha256, cancellationToken).ConfigureAwait(false))
                return "The downloaded PresentMon MSI failed its SHA-256 integrity check.";
        }

        var signature = MsiSignatureVerifier.Check(msiPath);
        var signatureProblem = DescribeSignatureProblem(signature);
        if (signatureProblem is not null)
            return signatureProblem;

        var result = await InstallMsiAsync(msiPath, cancellationToken).ConfigureAwait(false);
        return result.Success ? null : result.Message;
    }

    private static string? DescribeSignatureProblem(MsiSignatureResult signature)
    {
        switch (signature.Status)
        {
            case MsiSignatureStatus.Invalid:
                return "The downloaded PresentMon MSI has an invalid digital signature.";

            case MsiSignatureStatus.Valid
                when signature.Signer is null ||
                     !signature.Signer.Contains(ExpectedSignerName, StringComparison.OrdinalIgnoreCase):
                return $"The downloaded PresentMon MSI is signed by an unexpected publisher ({signature.Signer ?? "unknown"}).";

            case MsiSignatureStatus.Unsigned when RequireSignedMsi:
                return "The downloaded PresentMon MSI is not digitally signed.";

            default:
                return null;
        }
    }

    private static PresentMonStartupResult Fail(Version? installed, string message) =>
        installed is null
            ? PresentMonStartupResult.MissingFailed(message)
            : PresentMonStartupResult.Failed(installed, message);

    private async Task<List<PresentMonRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            GitHubReleasesUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
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

        var releases = await JsonSerializer.DeserializeAsync<List<PresentMonRelease>>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return releases ?? throw new InvalidOperationException("GitHub returned an empty release response.");
    }

    private static (PresentMonRelease Release, Version Version)? SelectRelease(IEnumerable<PresentMonRelease> releases)
    {
        PresentMonRelease? best = null;
        Version? bestVersion = null;

        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease)
                continue;

            if (!Version.TryParse(NormalizeVersion(release.TagName), out var version))
                continue;

            if (version.Major > MaxAutoInstallReleaseMajor)
                continue;

            if (SelectMsi(release) is null)
                continue;

            if (bestVersion is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best is null || bestVersion is null ? null : (best, bestVersion);
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        await source.CopyToAsync(target, timeout.Token).ConfigureAwait(false);
    }

    private async Task<string?> ResolveExpectedSha256Async(
        PresentMonRelease release,
        PresentMonAsset msiAsset,
        CancellationToken cancellationToken)
    {
        var fromDigest = ChecksumParser.NormalizeDigest(msiAsset.Digest);
        if (fromDigest is not null)
            return fromDigest;

        var checksumAsset = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
            a.Name.Contains("checksum", StringComparison.OrdinalIgnoreCase) ||
            a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase));

        if (checksumAsset is not null && !string.IsNullOrWhiteSpace(checksumAsset.BrowserDownloadUrl))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ChecksumTimeout);

                using var response = await _httpClient.GetAsync(
                    checksumAsset.BrowserDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(timeout.Token)
                        .ConfigureAwait(false);

                    var hash = ChecksumParser.FindHashForFile(text, msiAsset.Name, allowLoneHash: true);
                    if (hash is not null)
                        return hash;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return ChecksumParser.FindHashForFile(release.Body, msiAsset.Name, allowLoneHash: false);
    }

    private static async Task<bool> VerifySha256Async(
        FileStream stream,
        string expectedHex,
        CancellationToken cancellationToken)
    {
        if (!ChecksumParser.IsSha256Hex(expectedHex))
            return false;

        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expectedHex));
    }

    private static async Task<MsiInstallResult> InstallMsiAsync(
        string msiPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            Arguments = $"/i \"{msiPath}\" /qn /norestart",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new MsiInstallResult(
                false,
                "The PresentMon installation was cancelled at the administrator prompt.",
                ErrorCancelled);
        }

        if (process is null)
            throw new InvalidOperationException("Windows Installer could not be started.");

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(InstallTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new MsiInstallResult(
                    false,
                    $"The PresentMon installer did not finish within {InstallTimeout.TotalMinutes:0} minutes.",
                    -1);
            }

            return process.ExitCode is 0 or 3010
                ? new MsiInstallResult(true, string.Empty, process.ExitCode)
                : new MsiInstallResult(
                    false,
                    $"Windows Installer exited with code {process.ExitCode}.",
                    process.ExitCode);
        }
    }

    private static Version? GetInstalledVersion()
    {
        foreach (var path in PresentMonNative.GetCandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (Version.TryParse(NormalizeVersion(fileVersion), out var version))
                    return NormalizeToReleaseVersion(version);
            }
            catch
            {
            }
        }

        return null;
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

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

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

    private readonly record struct MsiInstallResult(bool Success, string Message, int ExitCode);
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