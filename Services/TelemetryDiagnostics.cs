using System.Diagnostics;
using Avalonia.Threading;

namespace Clockwork.Services;

/// <summary>
/// Persists telemetry failures and unhandled exceptions that would otherwise only be visible in a
/// debugger. The log is deliberately best-effort: diagnostics must never prevent monitoring.
/// </summary>
internal static class TelemetryDiagnostics
{
    private const long MaxLogBytes = 1024 * 1024; // 1 MB

    private static readonly object s_sync = new();
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clockwork",
        "telemetry.log");
    private static readonly string s_backupPath = s_path + ".old";

    private static int s_processHandlersRegistered;
    private static int s_dispatcherHandlerRegistered;

    internal static void RegisterUnhandledExceptionHandlers()
    {
        if (Interlocked.Exchange(ref s_processHandlersRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        if (Interlocked.CompareExchange(ref s_dispatcherHandlerRegistered, 1, 0) == 0)
        {
            try
            {
                Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            }
            catch
            {
                Interlocked.Exchange(ref s_dispatcherHandlerRegistered, 0);
            }
        }
    }

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
                RotateIfNeeded();
                var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
                File.AppendAllText(s_path, line);
            }
        }
        catch
        {
            // Logging is intentionally non-fatal.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(s_path);
            if (info.Exists && info.Length >= MaxLogBytes)
                File.Move(s_path, s_backupPath, overwrite: true);
        }
        catch
        {
            // Best-effort rotation; if locked or inaccessible, continue appending.
        }
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var detail = e.ExceptionObject is Exception ex
            ? ex.ToString()
            : e.ExceptionObject?.ToString() ?? "Unknown exception object";
        Write($"Unhandled AppDomain exception (terminating={e.IsTerminating}): {detail}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write($"Unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write($"Unhandled UI dispatcher exception: {e.Exception}");
    }
}