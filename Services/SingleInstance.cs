using System.Diagnostics.CodeAnalysis;

namespace Clockwork.Services;

/// <summary>
/// Ensures only one Clockwork process runs per Windows session. A second launch
/// signals the first instance to show its window, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Id = "Clockwork-6F3B1C2E-4A7D-4E58-9B0A-2D5C8E1F7A34";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex? mutex, EventWaitHandle? showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// Returns true if this process is the first instance. Otherwise the running
    /// instance is asked to show its window and false is returned.
    /// </summary>
    public static bool TryAcquire([NotNullWhen(true)] out SingleInstance? instance)
    {
        TelemetryDiagnostics.RegisterUnhandledExceptionHandlers();

        EventWaitHandle? showEvent = null;
        Mutex? mutex = null;
        bool acquired;

        try
        {
            showEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, $@"Local\{Id}-show");
            mutex = new Mutex(false, $@"Local\{Id}-mutex");
            try
            {
                acquired = mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
        }
        catch (Exception ex)
        {
            TelemetryDiagnostics.Write($"Single-instance acquisition failed; continuing without single-instance lock: {ex.Message}");
            showEvent?.Dispose();
            mutex?.Dispose();
            instance = new SingleInstance(null, null);
            return true;
        }

        if (!acquired)
        {
            try
            {
                showEvent.Set();
            }
            catch (Exception ex)
            {
                TelemetryDiagnostics.Write($"Failed to signal running Clockwork instance: {ex.Message}");
            }
            finally
            {
                showEvent.Dispose();
                mutex.Dispose();
            }

            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex, showEvent);
        return true;
    }

    /// <summary>Runs <paramref name="onActivate"/> (on a background thread) whenever another launch is attempted.</summary>
    public void StartListening(Action onActivate)
    {
        if (_showEvent is null || _disposed)
            return;

        var handles = new WaitHandle[] { _showEvent, _cts.Token.WaitHandle };

        _listener = new Thread(() =>
        {
            try
            {
                while (WaitHandle.WaitAny(handles) == 0)
                {
                    try
                    {
                        onActivate();
                    }
                    catch (Exception ex)
                    {
                        TelemetryDiagnostics.Write($"Single-instance activation callback failed: {ex}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                TelemetryDiagnostics.Write($"Single-instance listener stopped unexpectedly: {ex}");
            }
        })
        {
            IsBackground = true,
            Name = "Clockwork single-instance listener",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        // Let the listener leave WaitAny before the handles it waits on are disposed.
        try { _listener?.Join(TimeSpan.FromSeconds(1)); }
        catch (ThreadStateException) { }

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            catch (ObjectDisposedException) { }

            _mutex.Dispose();
        }

        _showEvent?.Dispose();
        _cts.Dispose();
    }
}