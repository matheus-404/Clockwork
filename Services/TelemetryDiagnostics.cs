using System.Diagnostics;

namespace Clockwork.Services;

/// <summary>
/// Persists telemetry failures that would otherwise only be visible in a debugger.
/// The log is deliberately best-effort: diagnostics must never prevent monitoring.
/// </summary>
internal static class TelemetryDiagnostics
{
    private static readonly object s_sync = new();
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clockwork",
        "telemetry.log");

    internal static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Debug.WriteLine($"[Clockwork] {message}");

        try
        {
            lock (s_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
                var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
                File.AppendAllText(s_path, line);
            }
        }
        catch
        {
            // Logging is intentionally non-fatal.
        }
    }
}
