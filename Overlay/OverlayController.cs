using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Clockwork.Services;
using Clockwork.ViewModels;
using Clockwork.Views;

namespace Clockwork.Overlay;

/// <summary>
/// Tracks the foreground game and feeds the overlay. All process enumeration and PresentMon/PDH
/// collection happens on background workers; the UI dispatcher is used only for visual updates.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private static readonly HashSet<string> LatencyStats = new(StringComparer.Ordinal)
    {
        "GPU Latency",
        "Display Latency",
        "Render/Present Latency",
        "Time Until Displayed",
        "Between Presents",
        "Between Display Changes",
        "Click-to-Photon Latency",
        "All Input-to-Photon Latency",
    };


    private readonly MainWindowViewModel _settings;
    private readonly OverlayViewModel _vm = new();
    private readonly PresentMonMonitor _presentMon = new();
    private readonly ProcessorFrequencyMonitor _processorFrequency = new();
    private readonly Win32.ProcessSnapshotTable _processSnapshots = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _fastWake = new(false);
    private readonly object _stateSync = new();
    private readonly object _presentMonSync = new();
    private readonly object _lineCacheSync = new();
    private readonly Dictionary<string, OverlayLine> _lineByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastPostedValues = new(StringComparer.Ordinal);

    // Settings version the posted-value cache belongs to. Guarded by _lineCacheSync.
    private int _lastPostedVersion;

    // Game detection and window placement: process-table refresh, window state, show/hide.
    private const int DetectionIntervalMs = 500;

    // Telemetry (PresentMon poll, PDH, RAM and so on). FPS, latency, playtime and system time
    // run on the separate 20 ms fast loop.
    private const int TelemetryIntervalMs = 100;

    // While the overlay is not visible nothing is displayed, so telemetry polls at the
    // detection rate instead (frames are still consumed, so Avg FPS keeps accumulating).
    private const int HiddenTelemetryDivisor = DetectionIntervalMs / TelemetryIntervalMs;

    private readonly Task _detectionTask;
    private readonly Task _telemetryTask;
    private readonly Task _fastTask;

    private EnabledStatsSnapshot _enabledStats;
    private int _enabledStatsVersion;
    private int _gamePid;
    private GameInfo? _trackedGame;
    private long _sessionStartTimestamp;
    private int _trackedRefreshMisses;
    private bool _overlayWanted;
    private int _hiddenTelemetryTicks;
    private int _lastSystemTimeStamp = -1;
    private string _lastSystemTimeText = string.Empty;

    // A transient process-table failure should not immediately end a running session.
    private const int MaxTrackedRefreshMisses = 8; // 4 seconds at the 500 ms detection loop.

    private OverlayWindow? _window;
    private GameInfo _lastUiGame;
    private bool _hasLastUiGame;
    private bool _disposed;
    private bool _fastLoopActive;
    private int _lastAppliedPositionX = int.MinValue;
    private int _lastAppliedPositionY = int.MinValue;

    public OverlayController(MainWindowViewModel settings, MainWindow _)
    {
        _settings = settings;
        _enabledStats = BuildEnabledStatsSnapshot();
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        foreach (var section in _settings.Sections)
        {
            foreach (var option in section.Options)
                option.PropertyChanged += OnOptionPropertyChanged;
        }

        SyncLines(_enabledStats.Names);
        _detectionTask = Task.Run(DetectionLoopAsync);
        _telemetryTask = Task.Run(TelemetryLoopAsync);
        _fastTask = Task.Run(FastTelemetryLoopAsync);
    }

    private void OnOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not OptionViewModel || e.PropertyName != nameof(OptionViewModel.IsOn) || _disposed)
            return;

        var snapshot = BuildEnabledStatsSnapshot();
        Volatile.Write(ref _enabledStats, snapshot);
        var newVersion = Interlocked.Increment(ref _enabledStatsVersion);
        lock (_lineCacheSync)
        {
            _lastPostedValues.Clear();
            _lastPostedVersion = newVersion;
        }

        PostUi(() =>
        {
            if (_disposed) return;
            SyncLines(snapshot.Names);
            UpdateFastLoopState();
        });

        _fastWake.Set();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.PropertyName is nameof(MainWindowViewModel.OverlayPositionX)
            or nameof(MainWindowViewModel.OverlayPositionY)
            or nameof(MainWindowViewModel.OverlayScale)
            or nameof(MainWindowViewModel.OverlayBackgroundEnabled)
            or nameof(MainWindowViewModel.OverlayBackgroundOpacity))
        {
            PostUi(() =>
            {
                if (_disposed || !_hasLastUiGame || _window is not { IsVisible: true })
                    return;
                ApplyOverlayPlacement(_lastUiGame);
            });
        }
    }

    private async Task DetectionLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(DetectionIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try { UpdateDetection(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[Clockwork] Detection tick failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clockwork] Detection loop stopped: {ex}");
        }
    }

    private async Task TelemetryLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TelemetryIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try { UpdateTelemetry(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[Clockwork] Telemetry tick failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clockwork] Telemetry loop stopped: {ex}");
        }
    }

    private async Task FastTelemetryLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!Volatile.Read(ref _fastLoopActive))
                {
                    _fastWake.Wait(_cts.Token);
                    _fastWake.Reset();
                    continue;
                }

                await Task.Delay(20, _cts.Token);
                if (!Volatile.Read(ref _fastLoopActive))
                    continue;

                try { UpdateFastTelemetry(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[Clockwork] Fast telemetry tick failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clockwork] Fast telemetry loop stopped: {ex}");
        }
    }

    /// <summary>
    /// Runs every 500 ms: finds and tracks the game, manages the session, and decides whether
    /// the overlay is shown and where it sits. It does not read any telemetry values.
    /// </summary>
    private void UpdateDetection()
    {
        // Read the version first: the snapshot is always written before the version is bumped, so
        // the snapshot read here is at least as new as this version.
        var version = Volatile.Read(ref _enabledStatsVersion);
        var enabled = Volatile.Read(ref _enabledStats);

        if (enabled.Names.Length == 0)
        {
            EndSession();
            Volatile.Write(ref _overlayWanted, false);
            PostUi(() =>
            {
                HideOverlayOnly();
                UpdateFastLoopState();
            });
            return;
        }

        if (!TryGetTrackedGame(out var game))
        {
            EndSession();
            Volatile.Write(ref _overlayWanted, false);
            PostUi(() =>
            {
                HideOverlayOnly();
                UpdateFastLoopState();
            });
            return;
        }

        lock (_stateSync)
        {
            if (game.Pid != _gamePid)
            {
                _gamePid = game.Pid;
                _trackedGame = game;
                _trackedRefreshMisses = 0;
                _sessionStartTimestamp = Stopwatch.GetTimestamp();
                _lastAppliedPositionX = int.MinValue;
                _lastAppliedPositionY = int.MinValue;
                lock (_presentMonSync)
                    _presentMon.StopTracking();
            }
            else
            {
                _trackedGame = game;
            }
        }

        var showOverlay = game.IsForeground && game.IsWindowVisible && !game.IsMinimized;
        Volatile.Write(ref _overlayWanted, showOverlay);
        PostUi(() => ApplyDetectionUpdate(version, game, showOverlay));
    }

    /// <summary>
    /// Runs every 100 ms: starts/updates PresentMon tracking for the current plan, reads the
    /// telemetry snapshot and posts the changed values. FPS, latency, playtime and system time
    /// are handled by the 20 ms fast loop instead.
    /// </summary>
    private void UpdateTelemetry()
    {
        var version = Volatile.Read(ref _enabledStatsVersion);
        var enabled = Volatile.Read(ref _enabledStats);
        if (enabled.Names.Length == 0)
            return;

        GameInfo game;
        lock (_stateSync)
        {
            if (_trackedGame is not { } tracked)
                return;
            game = tracked;
        }

        // Nothing is displayed while the overlay is hidden, so poll at the detection rate then.
        if (!Volatile.Read(ref _overlayWanted) && ++_hiddenTelemetryTicks < HiddenTelemetryDivisor)
            return;
        _hiddenTelemetryTicks = 0;

        PresentMonSnapshot snapshot;
        lock (_presentMonSync)
        {
            // The session may have ended or switched games since the game was read above.
            if (Volatile.Read(ref _gamePid) != game.Pid)
                return;

            if (enabled.MetricPlan.HasAny)
            {
                _presentMon.StartTracking(game.Pid, enabled.MetricPlan);
                _presentMon.Update();
            }
            else if (_presentMon.IsTracking)
            {
                _presentMon.StopTracking();
            }

            snapshot = _presentMon.GetSnapshot(enabled.MetricPlan);
        }

        if (enabled.CpuFrequencyEnabled)
        {
            var frequencies = _processorFrequency.Sample();
            snapshot = snapshot with { CpuCoreFrequenciesMHz = frequencies };
        }

        var updates = new List<LineValueUpdate>(enabled.Names.Length);
        foreach (var name in enabled.Names)
        {
            if (IsFastOwned(name))
                continue;

            var value = Read(name, game, snapshot);
            if (MarkValueChanged(name, value, version))
                updates.Add(new LineValueUpdate(name, value));
        }

        if (updates.Count == 0)
            return;

        PostUi(() => ApplyLineValues(version, updates.ToArray()));
    }

    private static bool IsFastOwned(string name) =>
        name is "FPS" or "System Time" or "Session Playtime" || LatencyStats.Contains(name);

    private void UpdateFastTelemetry()
    {
        var version = Volatile.Read(ref _enabledStatsVersion);
        var enabled = Volatile.Read(ref _enabledStats);
        if (!enabled.FastPlan.HasAny && !enabled.PlaytimeEnabled && !enabled.SystemTimeEnabled)
            return;

        GameInfo? game;
        long sessionStart;
        lock (_stateSync)
        {
            game = _trackedGame;
            sessionStart = _sessionStartTimestamp;
        }

        if (game is null)
        {
            PostUi(UpdateFastLoopState);
            return;
        }

        PresentMonFastFrameSnapshot fast;
        if (enabled.FastPlan.HasAny)
        {
            lock (_presentMonSync)
            {
                _presentMon.UpdateFastFrameMetrics();
                fast = _presentMon.GetFastFrameSnapshot(enabled.FastPlan);
            }
        }
        else
        {
            fast = new PresentMonFastFrameSnapshot(null, null, null, null, null, null, null, null, null);
        }

        var updates = new List<LineValueUpdate>(10);
        if (enabled.FastPlan.Fps)
        {
            var fpsText = fast.Fps is { } fps && double.IsFinite(fps)
                ? fps.ToString("0")
                : "N/A";
            if (MarkValueChanged("FPS", fpsText, version))
                updates.Add(new LineValueUpdate("FPS", fpsText));
        }

        foreach (var name in LatencyStats)
        {
            if (!enabled.Stats.Contains(name))
                continue;

            var value = name switch
            {
                "GPU Latency" => FormatFastLatency(fast.GpuLatencyMs),
                "Display Latency" => FormatFastLatency(fast.DisplayLatencyMs),
                "Render/Present Latency" => FormatFastLatency(fast.RenderPresentLatencyMs),
                "Time Until Displayed" => FormatFastLatency(fast.UntilDisplayedMs),
                "Between Presents" => FormatFastLatency(fast.BetweenPresentsMs),
                "Between Display Changes" => FormatFastLatency(fast.BetweenDisplayChangeMs),
                "Click-to-Photon Latency" => FormatFastLatency(fast.ClickToPhotonLatencyMs),
                "All Input-to-Photon Latency" => FormatFastLatency(fast.AllInputToPhotonLatencyMs),
                _ => "N/A",
            };
            if (MarkValueChanged(name, value, version))
                updates.Add(new LineValueUpdate(name, value));
        }

        if (enabled.PlaytimeEnabled && sessionStart != 0)
        {
            var playtime = FormatPlaytime(GetSessionElapsed(sessionStart));
            if (MarkValueChanged("Session Playtime", playtime, version))
                updates.Add(new LineValueUpdate("Session Playtime", playtime));
        }

        if (enabled.SystemTimeEnabled)
        {
            var now = DateTime.Now;
            var stamp = now.Hour * 60 + now.Minute;
            if (stamp != _lastSystemTimeStamp)
            {
                _lastSystemTimeStamp = stamp;
                _lastSystemTimeText = now.ToString("HH:mm");
            }

            if (MarkValueChanged("System Time", _lastSystemTimeText, version))
                updates.Add(new LineValueUpdate("System Time", _lastSystemTimeText));
        }

        if (updates.Count == 0)
            return;

        PostUi(() => ApplyLineValues(version, updates.ToArray()));
    }

    private bool TryGetTrackedGame(out GameInfo game)
    {
        lock (_stateSync)
        {
            if (_trackedGame is { } tracked)
            {
                if (GameDetector.IsExecutableBlacklisted(tracked.Name))
                {
                    game = default;
                    return false;
                }

                if (GameDetector.TryRefreshTracked(_processSnapshots, tracked, out var refreshed))
                {
                    _trackedRefreshMisses = 0;
                    _trackedGame = refreshed;
                    game = refreshed;
                    return true;
                }

                _trackedRefreshMisses++;
                if (_trackedRefreshMisses > MaxTrackedRefreshMisses)
                {
                    game = default;
                    return false;
                }

                if (!GameDetector.TryRefreshTrackedWindowOnly(tracked, out var windowRefreshed))
                {
                    windowRefreshed = tracked with
                    {
                        IsForeground = false,
                        IsWindowVisible = false,
                        IsMinimized = true,
                    };
                }

                _trackedGame = windowRefreshed;
                game = windowRefreshed;
                return true;
            }
        }

        if (GameDetector.TryGet(_processSnapshots, out game))
            return true;

        game = default;
        return false;
    }

    private EnabledStatsSnapshot BuildEnabledStatsSnapshot()
    {
        var names = _settings.Sections
            .SelectMany(s => s.Options)
            .Where(o => o.IsOn)
            .Select(o => o.Name)
            .ToArray();

        var stats = names.ToHashSet(StringComparer.Ordinal);

        return new EnabledStatsSnapshot(
            names,
            stats,
            stats.Contains("Session Playtime"),
            stats.Contains("System Time"),
            stats.Contains("CPU Frequency"),
            BuildFastPlan(stats),
            BuildMetricPlan(stats));
    }

    private static PresentMonFastFramePlan BuildFastPlan(ISet<string> stats) => new(
        stats.Contains("FPS"),
        stats.Contains("GPU Latency"),
        stats.Contains("Display Latency"),
        stats.Contains("Render/Present Latency"),
        stats.Contains("Time Until Displayed"),
        stats.Contains("Between Presents"),
        stats.Contains("Between Display Changes"),
        stats.Contains("Click-to-Photon Latency"),
        stats.Contains("All Input-to-Photon Latency"));

    private static PresentMonMetricPlan BuildMetricPlan(ISet<string> s) => new(
        s.Contains("FPS"),
        s.Contains("Avg FPS"),
        s.Contains("1% Low FPS"),
        s.Contains("0.1% Low FPS"),
        s.Contains("Frame Time"),
        s.Contains("Dropped Frames"),
        s.Contains("Presented FPS"),
        s.Contains("Displayed FPS"),
        s.Contains("Application FPS"),
        s.Contains("CPU Usage"),
        s.Contains("CPU Busy"),
        s.Contains("CPU Wait"),
        s.Contains("CPU Frame Time"),
        s.Contains("GPU Temperature"),
        s.Contains("GPU Core Clock Frequency"),
        s.Contains("GPU Memory Clock Frequency"),
        s.Contains("GPU VRAM Usage"),
        s.Contains("VRAM Usage (%)"),
        s.Contains("GPU Power"),
        s.Contains("GPU Usage"),
        s.Contains("GPU Render/Compute Utilization"),
        s.Contains("GPU Power Limited"),
        s.Contains("GPU Temperature Limited"),
        s.Contains("GPU Current Limited"),
        s.Contains("GPU Voltage Limited"),
        s.Contains("GPU Utilization Limited"),
        s.Contains("GPU Busy"),
        s.Contains("GPU Wait"),
        s.Contains("GPU Time"),
        s.Contains("GPU Latency"),
        s.Contains("Display Latency"),
        s.Contains("Render/Present Latency"),
        s.Contains("Time Until Displayed"),
        s.Contains("Between Presents"),
        s.Contains("Between Display Changes"),
        s.Contains("Click-to-Photon Latency"),
        s.Contains("All Input-to-Photon Latency"));

    private void PostUi(Action action)
    {
        if (_disposed)
            return;

        Dispatcher.UIThread.Post(action);
    }

    private bool MarkValueChanged(string label, string value, int version)
    {
        lock (_lineCacheSync)
        {
            // A tick that began before the latest settings change would be discarded by the UI
            // (version mismatch). It must neither post nor touch the cache, otherwise the cache
            // claims a value was delivered to a line that was rebuilt and is still empty.
            if (version != _lastPostedVersion)
                return false;

            if (_lastPostedValues.TryGetValue(label, out var previous) &&
                string.Equals(previous, value, StringComparison.Ordinal))
                return false;

            _lastPostedValues[label] = value;
            return true;
        }
    }

    private void ApplyDetectionUpdate(int version, GameInfo game, bool showOverlay)
    {
        if (_disposed || version != Volatile.Read(ref _enabledStatsVersion))
            return;

        _lastUiGame = game;
        _hasLastUiGame = true;
        SyncLines(Volatile.Read(ref _enabledStats).Names);

        if (showOverlay)
        {
            _window ??= new OverlayWindow { DataContext = _vm };
            if (!_window.IsVisible)
                _window.Show();
            ApplyOverlayPlacement(game);
        }
        else
        {
            HideOverlayOnly();
        }

        UpdateFastLoopState();
    }

    private void ApplyLineValues(int version, IReadOnlyList<LineValueUpdate> updates)
    {
        if (_disposed || version != Volatile.Read(ref _enabledStatsVersion))
            return;

        // Cheap when nothing changed; guarantees the lines exist before values are applied.
        SyncLines(Volatile.Read(ref _enabledStats).Names);

        foreach (var item in updates)
        {
            if (_lineByLabel.TryGetValue(item.Label, out var line) && line.Value != item.Value)
                line.Value = item.Value;
        }
    }

    private void SyncLines(IReadOnlyList<string> names)
    {
        var lines = _vm.Lines;

        if (lines.Count == names.Count)
        {
            var same = true;
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(lines[i].Label, names[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return;
        }

        // Update the collection in place so lines that stay keep their current values and only
        // the lines that were actually toggled are added, removed or moved.
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(lines[i].Label))
            {
                _lineByLabel.Remove(lines[i].Label);
                lines.RemoveAt(i);
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (i < lines.Count && string.Equals(lines[i].Label, name, StringComparison.Ordinal))
                continue;

            if (_lineByLabel.TryGetValue(name, out var existing))
            {
                var from = lines.IndexOf(existing);
                if (from >= 0 && from != i)
                    lines.Move(from, i);
                continue;
            }

            OverlayLine line = name == "CPU Frequency"
                ? new PerCoreOverlayLine(name)
                : new OverlayLine(name);
            lines.Insert(i, line);
            _lineByLabel[name] = line;
        }
    }

    private void ApplyOverlayPlacement(GameInfo game)
    {
        if (_window is null)
            return;

        _window.SetScale(_settings.OverlayScale / 100.0);
        _window.SetAppearance(_settings.OverlayBackgroundEnabled, _settings.OverlayBackgroundOpacity);

        // Recalculate this every slow telemetry update because the game may move or resize even
        // while its PID remains unchanged. The actual Position assignment is cached below.
        if (game.WindowHandle != 0 &&
            Win32.TryGetClientAreaScreenRect(game.WindowHandle, out var clientRect))
        {
            var clientWidth = Math.Max(0, clientRect.Right - clientRect.Left);
            var clientHeight = Math.Max(0, clientRect.Bottom - clientRect.Top);
            var scaling = Math.Max(0.01, _window.RenderScaling);
            var overlayWidth = Math.Max(0, _window.Bounds.Width * scaling);
            var overlayHeight = Math.Max(0, _window.Bounds.Height * scaling);

            var maxX = Math.Max(0, clientWidth - overlayWidth);
            var maxY = Math.Max(0, clientHeight - overlayHeight);
            var normalizedX = Math.Clamp(_settings.OverlayPositionX / 100.0, 0.0, 1.0);
            var normalizedY = Math.Clamp(_settings.OverlayPositionY / 100.0, 0.0, 1.0);

            var x = clientRect.Left + (int)Math.Round(maxX * normalizedX);
            var y = clientRect.Top + (int)Math.Round(maxY * normalizedY);
            SetOverlayPositionCached(x, y);
            return;
        }

        SetOverlayPositionCached(game.Monitor.Left + 16, game.Monitor.Top + 16);
    }

    private void SetOverlayPositionCached(int x, int y)
    {
        if (_window is null)
            return;

        if (_lastAppliedPositionX == x && _lastAppliedPositionY == y)
            return;

        _window.Position = new PixelPoint(x, y);
        _lastAppliedPositionX = x;
        _lastAppliedPositionY = y;
    }

    private void UpdateFastLoopState()
    {
        var enabled = Volatile.Read(ref _enabledStats);
        var active = _window is { IsVisible: true } &&
                     (enabled.FastPlan.HasAny || enabled.PlaytimeEnabled || enabled.SystemTimeEnabled);
        Volatile.Write(ref _fastLoopActive, active);
        if (active)
            _fastWake.Set();
    }

    private void HideOverlayOnly()
    {
        if (_window is { IsVisible: true })
            _window.Hide();
        Volatile.Write(ref _fastLoopActive, false);
    }

    private void EndSession()
    {
        lock (_stateSync)
        {
            _gamePid = 0;
            _trackedGame = null;
            _trackedRefreshMisses = 0;
            _sessionStartTimestamp = 0;
            _lastAppliedPositionX = int.MinValue;
            _lastAppliedPositionY = int.MinValue;
        }

        lock (_presentMonSync)
        {
            if (_presentMon.IsTracking)
                _presentMon.StopTracking();
        }
    }

    private TimeSpan GetSessionElapsed()
    {
        lock (_stateSync)
            return GetSessionElapsed(_sessionStartTimestamp);
    }

    private static TimeSpan GetSessionElapsed(long startTimestamp)
    {
        if (startTimestamp == 0)
            return TimeSpan.Zero;

        var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return TimeSpan.FromSeconds(Math.Max(0, elapsedTicks) / (double)Stopwatch.Frequency);
    }

    private static string FormatFastLatency(double? value) =>
        value is { } v && double.IsFinite(v) ? $"{v:0.0} ms" : "N/A";

    private static string FormatPlaytime(TimeSpan elapsed)
    {
        var totalHours = (long)elapsed.TotalHours;
        return $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private string Read(string stat, GameInfo game, PresentMonSnapshot snapshot)
    {
        const string na = "N/A";
        string Num(double? value, string format, string unit) =>
            value is { } v && double.IsFinite(v) ? v.ToString(format) + unit : na;
        string Cores(IReadOnlyList<double> values, string unit) =>
            values.Count > 0
                ? string.Join(Environment.NewLine, values.Select((v, i) => $"CPU {i}: {v:0}{unit}"))
                : na;
        string Flag(double? value) =>
            value is { } v && double.IsFinite(v) ? (v != 0 ? "Yes" : "No") : na;
        string PresentMonValue(double? value, PresentMonNative.PM_METRIC metric, string format, string unit) =>
            value is { } v && double.IsFinite(v) ? v.ToString(format) + unit : _presentMon.GetMetricStatusText(metric) ?? na;
        string PresentMonFlag(double? value, PresentMonNative.PM_METRIC metric) =>
            value is { } v && double.IsFinite(v) ? (v != 0 ? "Yes" : "No") : _presentMon.GetMetricStatusText(metric) ?? na;

        return stat switch
        {
            "FPS" => Num(snapshot.Fps, "0", ""),
            "Avg FPS" => Num(snapshot.AvgFps, "0", ""),
            "1% Low FPS" => Num(snapshot.Low1Fps, "0", ""),
            "0.1% Low FPS" => Num(snapshot.Low01Fps, "0", ""),
            "Frame Time" => Num(snapshot.FrameTimeMs, "0.0", " ms"),
            "Dropped Frames" => Flag(snapshot.DroppedFrames),
            "Presented FPS" => Num(snapshot.PresentedFps, "0", ""),
            "Displayed FPS" => Num(snapshot.DisplayedFps, "0", ""),
            "Application FPS" => Num(snapshot.ApplicationFps, "0", ""),

            "CPU Frequency" => Cores(snapshot.CpuCoreFrequenciesMHz, " MHz"),
            "CPU Usage" => Num(snapshot.CpuUsagePercent, "0", "%"),
            "CPU Busy" => Num(snapshot.CpuBusyMs, "0.0", " ms"),
            "CPU Wait" => Num(snapshot.CpuWaitMs, "0.0", " ms"),
            "CPU Frame Time" => Num(snapshot.CpuFrameTimeMs, "0.0", " ms"),

            "GPU Temperature" => Num(snapshot.GpuTemperatureC, "0", "°C"),
            "GPU Core Clock Frequency" => Num(snapshot.GpuCoreClockMHz, "0", " MHz"),
            "GPU Memory Clock Frequency" => Num(snapshot.GpuMemoryClockMHz, "0", " MHz"),
            "GPU VRAM Usage" => Num(snapshot.GpuVramUsedMb, "0", " MB"),
            "VRAM Usage (%)" => Num(snapshot.GpuVramUsagePercent, "0", "%"),
            "GPU Power" => Num(snapshot.GpuPowerWatts, "0", " W"),
            "GPU Usage" => Num(snapshot.GpuUsagePercent, "0", "%"),
            "GPU Render/Compute Utilization" => PresentMonValue(snapshot.GpuRenderComputeUsagePercent, PresentMonNative.PM_METRIC.GPU_RENDER_COMPUTE_UTILIZATION, "0", "%"),
            "GPU Power Limited" => PresentMonFlag(snapshot.GpuPowerLimited, PresentMonNative.PM_METRIC.GPU_POWER_LIMITED),
            "GPU Temperature Limited" => PresentMonFlag(snapshot.GpuTemperatureLimited, PresentMonNative.PM_METRIC.GPU_TEMPERATURE_LIMITED),
            "GPU Current Limited" => PresentMonFlag(snapshot.GpuCurrentLimited, PresentMonNative.PM_METRIC.GPU_CURRENT_LIMITED),
            "GPU Voltage Limited" => PresentMonFlag(snapshot.GpuVoltageLimited, PresentMonNative.PM_METRIC.GPU_VOLTAGE_LIMITED),
            "GPU Utilization Limited" => PresentMonFlag(snapshot.GpuUtilizationLimited, PresentMonNative.PM_METRIC.GPU_UTILIZATION_LIMITED),
            "GPU Busy" => Num(snapshot.GpuBusyMs, "0.0", " ms"),
            "GPU Wait" => Num(snapshot.GpuWaitMs, "0.0", " ms"),
            "GPU Time" => Num(snapshot.GpuTimeMs, "0.0", " ms"),
            "GPU Latency" => Num(snapshot.GpuLatencyMs, "0.0", " ms"),

            "Display Latency" => Num(snapshot.DisplayLatencyMs, "0.0", " ms"),
            "Render/Present Latency" => Num(snapshot.RenderPresentLatencyMs, "0.0", " ms"),
            "Time Until Displayed" => Num(snapshot.UntilDisplayedMs, "0.0", " ms"),
            "Between Presents" => Num(snapshot.BetweenPresentsMs, "0.0", " ms"),
            "Between Display Changes" => Num(snapshot.BetweenDisplayChangeMs, "0.0", " ms"),
            "Click-to-Photon Latency" => Num(snapshot.ClickToPhotonLatencyMs, "0.0", " ms"),
            "All Input-to-Photon Latency" => Num(snapshot.AllInputToPhotonLatencyMs, "0.0", " ms"),

            "RAM Usage" => Win32.GetRamUsage() is { } ram ? $"{ram.usedGb:0.0} / {ram.totalGb:0.0} GB" : na,
            "RAM Usage (%)" => Win32.GetRamUsage() is { } percentRam ? $"{percentRam.percent:0}%" : na,
            "Process/Game RAM Usage" => game.WorkingSetBytes is { } bytes ? $"{bytes / 1073741824.0:0.0} GB" : na,

            "System Time" => DateTime.Now.ToString("HH:mm"),
            "Session Playtime" => FormatPlaytime(GetSessionElapsed()),
            _ => na,
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        foreach (var section in _settings.Sections)
        {
            foreach (var option in section.Options)
                option.PropertyChanged -= OnOptionPropertyChanged;
        }

        _cts.Cancel();
        _fastWake.Set();

        bool workersStopped;
        try { workersStopped = Task.WaitAll([_detectionTask, _telemetryTask, _fastTask], TimeSpan.FromSeconds(1)); }
        catch { workersStopped = true; } // WaitAll only throws once every task has finished.

        if (!workersStopped)
        {
            // A worker is stuck in a PresentMon/PDH call. Do not block shutdown on it, and do not
            // free native resources it may still be using; the OS reclaims them on process exit.
            _window?.Close();
            GC.SuppressFinalize(this);
            return;
        }

        EndSession();
        _window?.Close();
        lock (_presentMonSync)
            _presentMon.Dispose();
        _processorFrequency.Dispose();
        _processSnapshots.Dispose();
        _fastWake.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly record struct LineValueUpdate(string Label, string Value);

    private sealed class EnabledStatsSnapshot
    {
        public string[] Names { get; }
        public HashSet<string> Stats { get; }
        public bool PlaytimeEnabled { get; }
        public bool SystemTimeEnabled { get; }
        public bool CpuFrequencyEnabled { get; }
        public PresentMonFastFramePlan FastPlan { get; }
        public PresentMonMetricPlan MetricPlan { get; }

        public EnabledStatsSnapshot(
            string[] names,
            HashSet<string> stats,
            bool playtimeEnabled,
            bool systemTimeEnabled,
            bool cpuFrequencyEnabled,
            PresentMonFastFramePlan fastPlan,
            PresentMonMetricPlan metricPlan)
        {
            Names = names;
            Stats = stats;
            PlaytimeEnabled = playtimeEnabled;
            SystemTimeEnabled = systemTimeEnabled;
            CpuFrequencyEnabled = cpuFrequencyEnabled;
            FastPlan = fastPlan;
            MetricPlan = metricPlan;
        }
    }
}
