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
/// <remarks>
/// The MSI is installed with administrator rights, so the download is only trusted after:
/// a SHA-256 from the release (the install is refused when none can be found), an Authenticode
/// check, and holding the file open (write/delete denied) from hashing until the installer ran.
/// </remarks>
public sealed class PresentMonUpdateService : IDisposable
{
    private const string GitHubReleasesUrl =
        "https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=30";

    private const int ErrorCancelled = 1223; // user declined the UAC prompt
    private const int ErrorInstallPackageOpenFailed = 1619;

    /// <summary>The oldest PresentMon release Clockwork supports.</summary>
    private static readonly Version MinimumSupportedVersion = new(2, 3, 1);

    /// <summary>
    /// Releases newer than this major version are never installed automatically: Clockwork talks
    /// to PresentMon API major version 3, and a future major release may break that contract.
    /// </summary>
    private const int MaxAutoInstallReleaseMajor = 2;

    /// <summary>
    /// When false (the default) a PresentMon install that already works is left alone, so
    /// Clockwork never surprises you with an administrator prompt at launch. Clockwork only
    /// installs PresentMon when it is missing or older than <see cref="MinimumSupportedVersion"/>.
    /// </summary>
    private static readonly bool AutoUpdateWorkingInstall = false;

    /// <summary>
    /// When true an MSI without any digital signature is refused. It is off because it has not been
    /// confirmed that every PresentMon release is signed; a signature that is present must always
    /// be valid and from an expected publisher regardless of this setting.
    /// </summary>
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

        // A working install is left alone: no network, no prompt, instant startup.
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

            // Fail closed: with no way to verify the file, nothing is installed with admin rights.
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

    /// <summary>
    /// Verifies the downloaded file and installs it. Returns null on success or a message on failure.
    /// </summary>
    private static async Task<string?> VerifyAndInstallAsync(
        string msiPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        // From here until the installer has finished, the file is held open with writes and
        // deletes denied, so it cannot be swapped between the checks and the elevated install.
        var guard = OpenGuard(msiPath);
        try
        {
            if (!await VerifySha256Async(guard, expectedSha256, cancellationToken).ConfigureAwait(false))
                return "The downloaded PresentMon MSI failed its SHA-256 integrity check.";

            var signature = MsiSignatureVerifier.Check(msiPath, guard.SafeFileHandle.DangerousGetHandle());
            var signatureProblem = DescribeSignatureProblem(signature);
            if (signatureProblem is not null)
                return signatureProblem;

            var result = await InstallMsiAsync(msiPath, cancellationToken).ConfigureAwait(false);
            if (result.Success)
                return null;

            if (result.ExitCode != ErrorInstallPackageOpenFailed)
                return result.Message;
        }
        finally
        {
            await guard.DisposeAsync().ConfigureAwait(false);
        }

        // Windows Installer could not open the package while it was held open. Fall back to
        // re-verifying the file right before a second attempt without the guard.
        var recheck = OpenGuard(msiPath);
        try
        {
            if (!await VerifySha256Async(recheck, expectedSha256, cancellationToken).ConfigureAwait(false))
                return "The downloaded PresentMon MSI failed its SHA-256 integrity check.";
        }
        finally
        {
            await recheck.DisposeAsync().ConfigureAwait(false);
        }

        var retry = await InstallMsiAsync(msiPath, cancellationToken).ConfigureAwait(false);
        return retry.Success ? null : retry.Message;
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

    private static FileStream OpenGuard(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

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

    /// <summary>
    /// Picks the newest stable release Clockwork is allowed to install: not a draft or
    /// prerelease, a valid version, no newer than the supported major version, and with an MSI.
    /// </summary>
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

        // HttpClient.Timeout only covers waiting for the response headers here, not the body, so
        // the whole download gets its own deadline. A stalled transfer can never hang startup.
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

    /// <summary>
    /// Finds the expected SHA-256 of the MSI: the asset digest GitHub publishes first, then a
    /// checksum file in the release, then the release notes. Returns null when none matches.
    /// </summary>
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
            // An unanswered UAC prompt or a hung installer must not block startup forever.
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

        /// <summary>GitHub's own checksum for the asset, formatted "sha256:&lt;hex&gt;" (when present).</summary>
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
