namespace Clockwork.Services;

/// <summary>
/// Ensures only one Clockwork process runs per Windows session. A second launch
/// signals the first instance to show its window, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Id = "Clockwork-6F3B1C2E-4A7D-4E58-9B0A-2D5C8E1F7A34";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// Returns true if this process is the first instance. Otherwise the running
    /// instance is asked to show its window and false is returned.
    /// </summary>
    public static bool TryAcquire(out SingleInstance? instance)
    {
        var mutex = new Mutex(true, $@"Local\{Id}-mutex", out var createdNew);
        var showEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, $@"Local\{Id}-show");

        if (!createdNew)
        {
            try { showEvent.Set(); }
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
        var handles = new WaitHandle[] { _showEvent, _cts.Token.WaitHandle };

        new Thread(() =>
        {
            while (WaitHandle.WaitAny(handles) == 0)
                onActivate();
        })
        {
            IsBackground = true,
            Name = "Clockwork single-instance listener",
        }.Start();
    }

    // Must be called on the thread that acquired the mutex (Main).
    public void Dispose()
    {
        _cts.Cancel();

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }

        _mutex.Dispose();
        _showEvent.Dispose();
        _cts.Dispose();
    }
}
