using System.Globalization;

namespace Clockwork.Overlay;

/// <summary>
/// Turns values into the text shown on the overlay. Everything uses the invariant culture so the
/// overlay looks the same on every machine ("12.3 ms", never "12,3 ms").
/// </summary>
internal static class OverlayStatFormatter
{
    public const string NotAvailable = "N/A";

    public static string FormatNumber(double value, int decimals, string unit) =>
        string.Concat(value.ToString(decimals <= 0 ? "0" : "0.0", CultureInfo.InvariantCulture), unit);

    public static string FormatFlag(double value) => value != 0 ? "Yes" : "No";

    /// <summary>Formats whole seconds as HH:mm:ss (hours are not capped at 24).</summary>
    public static string FormatPlaytime(long totalSeconds)
    {
        if (totalSeconds < 0)
            totalSeconds = 0;

        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}");
    }

    /// <summary>Formats minutes since midnight as HH:mm.</summary>
    public static string FormatClock(long minutesOfDay) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutesOfDay / 60 % 24:00}:{minutesOfDay % 60:00}");

    public static string FormatRam(double usedGb, double totalGb) =>
        string.Create(CultureInfo.InvariantCulture, $"{usedGb:0.0} / {totalGb:0.0} GB");

    public static string FormatPercent(long percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{percent}%");

    /// <summary>Formats a size given in tenths of a gigabyte.</summary>
    public static string FormatTenthsOfGb(long tenths) =>
        string.Create(CultureInfo.InvariantCulture, $"{tenths / 10.0:0.0} GB");
}

/// <summary>
/// Tracks the last value shown for one overlay line so unchanged values are never re-formatted or
/// re-posted to the UI thread. A slot belongs to exactly one worker loop, so it needs no locking.
/// </summary>
internal sealed class StatSlot
{
    private bool _hasNumber;
    private double _lastNumber;
    private bool _hasKey;
    private long _lastKey;
    private string? _lastText;

    public StatSlot(StatDefinition definition) => Definition = definition;

    public StatDefinition Definition { get; }

    public string Label => Definition.Label;

    /// <summary>
    /// Updates a numeric statistic. The value is rounded to the displayed precision first, so a
    /// change smaller than what would be visible does not count as a change.
    /// </summary>
    public bool TryUpdateNumber(double? value, string? status, out string text)
    {
        if (value is { } v && double.IsFinite(v))
        {
            var rounded = Math.Round(v, Definition.Decimals, MidpointRounding.AwayFromZero);
            if (rounded == 0)
                rounded = 0; // normalise negative zero so it never prints as "-0"

            if (_hasNumber && rounded == _lastNumber)
            {
                text = string.Empty;
                return false;
            }

            _hasNumber = true;
            _hasKey = false;
            _lastNumber = rounded;
            text = _lastText = OverlayStatFormatter.FormatNumber(rounded, Definition.Decimals, Definition.Unit);
            return true;
        }

        return TryUpdateText(status ?? OverlayStatFormatter.NotAvailable, out text);
    }

    /// <summary>Updates a Yes/No statistic.</summary>
    public bool TryUpdateFlag(double? value, string? status, out string text) =>
        TryUpdateText(
            value is { } v && double.IsFinite(v)
                ? OverlayStatFormatter.FormatFlag(v)
                : status ?? OverlayStatFormatter.NotAvailable,
            out text);

    /// <summary>Updates a statistic whose displayed value is fully determined by an integer key.</summary>
    public bool TryUpdateKeyed(long key, Func<long, string> format, out string text)
    {
        if (_hasKey && key == _lastKey)
        {
            text = string.Empty;
            return false;
        }

        _hasKey = true;
        _hasNumber = false;
        _lastKey = key;
        text = _lastText = format(key);
        return true;
    }

    /// <summary>Updates a statistic by comparing the formatted text itself.</summary>
    public bool TryUpdateText(string candidate, out string text)
    {
        _hasNumber = false;
        _hasKey = false;

        if (string.Equals(_lastText, candidate, StringComparison.Ordinal))
        {
            text = string.Empty;
            return false;
        }

        text = _lastText = candidate;
        return true;
    }
}
