using System.Text.RegularExpressions;

namespace Clockwork.Services;

/// <summary>Pure helpers for finding the expected SHA-256 of a release asset.</summary>
internal static class ChecksumParser
{
    private static readonly Regex HashRegex = new(@"\b[a-fA-F0-9]{64}\b", RegexOptions.Compiled);

    /// <summary>
    /// Normalises a GitHub asset digest ("sha256:&lt;hex&gt;") or a bare hex string to lowercase hex.
    /// Returns null for anything that is not a SHA-256 digest.
    /// </summary>
    public static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];
        else if (value.Contains(':'))
            return null; // a different algorithm

        return IsSha256Hex(value) ? value.ToLowerInvariant() : null;
    }

    public static bool IsSha256Hex(string value) =>
        value.Length == 64 && HashRegex.IsMatch(value);

    /// <summary>
    /// Finds the SHA-256 for <paramref name="fileName"/> in checksum text. A line that mentions the
    /// file name wins. When <paramref name="allowLoneHash"/> is set, a checksum file that contains
    /// exactly one line holding nothing but a hash (a per-asset ".sha256" file) is accepted too.
    /// The first hash in the text is never assumed to belong to the file.
    /// </summary>
    public static string? FindHashForFile(string? text, string fileName, bool allowLoneHash)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fileName))
            return null;

        string? hashOnly = null;
        var hashOnlyLines = 0;
        var hashLines = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var match = HashRegex.Match(line);
            if (!match.Success)
                continue;

            hashLines++;

            if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                return match.Value.ToLowerInvariant();

            var remainder = line.Replace(match.Value, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim(' ', '\t', '*', '-', '`', '|');
            if (remainder.Length == 0)
            {
                hashOnlyLines++;
                hashOnly = match.Value.ToLowerInvariant();
            }
        }

        return allowLoneHash && hashLines == 1 && hashOnlyLines == 1 ? hashOnly : null;
    }
}
