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

    private readonly Task _slowTask;
    private readonly Task _fastTask;

    private EnabledStatsSnapshot _enabledStats;
    private int _enabledStatsVersion;
    private int _gamePid;
    private GameInfo? _trackedGame;
    private long _sessionStartTimestamp;
    private int _trackedRefreshMisses;

    // A transient process-table failure should not immediately end a running session.
    private const int MaxTrackedRefreshMisses = 8; // 4 seconds at the 500 ms slow loop.

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
        _slowTask = Task.Run(SlowTelemetryLoopAsync);
        _fastTask = Task.Run(FastTelemetryLoopAsync);
    }

    private void OnOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not OptionViewModel || e.PropertyName != nameof(OptionViewModel.IsOn) || _disposed)
            return;

        var snapshot = BuildEnabledStatsSnapshot();
        Volatile.Write(ref _enabledStats, snapshot);
        Interlocked.Increment(ref _enabledStatsVersion);
        lock (_lineCacheSync)
            _lastPostedValues.Clear();

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

    private async Task SlowTelemetryLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
                UpdateSlowTelemetry();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clockwork] Slow telemetry loop stopped: {ex}");
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

                UpdateFastTelemetry();
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

    private void UpdateSlowTelemetry()
    {
        var enabled = Volatile.Read(ref _enabledStats);
        var version = Volatile.Read(ref _enabledStatsVersion);

        if (enabled.Names.Length == 0)
        {
            EndSession();
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

        if (enabled.MetricPlan.HasAny)
        {
            lock (_presentMonSync)
            {
                _presentMon.StartTracking(game.Pid, enabled.MetricPlan);
                _presentMon.Update();
            }
        }
        else
        {
            lock (_presentMonSync)
            {
                if (_presentMon.IsTracking)
                    _presentMon.StopTracking();
            }
        }

        PresentMonSnapshot snapshot;
        lock (_presentMonSync)
        {
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
            if (name == "Session Playtime")
                continue;

            var value = Read(name, game, snapshot);
            if (MarkValueChanged(name, value))
                updates.Add(new LineValueUpdate(name, value));
        }

        if (enabled.PlaytimeEnabled)
        {
            var playtime = FormatPlaytime(GetSessionElapsed());
            if (MarkValueChanged("Session Playtime", playtime))
                updates.Add(new LineValueUpdate("Session Playtime", playtime));
        }

        var showOverlay = game.IsForeground && game.IsWindowVisible && !game.IsMinimized;
        var uiUpdate = new TelemetryUiUpdate(version, game, showOverlay, updates.ToArray());
        PostUi(() => ApplyTelemetryUpdate(uiUpdate));
    }

    private void UpdateFastTelemetry()
    {
        var enabled = Volatile.Read(ref _enabledStats);
        if (!enabled.FastPlan.HasAny && !enabled.PlaytimeEnabled)
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

        var updates = new List<LineValueUpdate>(9);
        if (enabled.FastPlan.Fps)
        {
            var fpsText = fast.Fps is { } fps && double.IsFinite(fps)
                ? fps.ToString("0")
                : "N/A";
            if (MarkValueChanged("FPS", fpsText))
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
            if (MarkValueChanged(name, value))
                updates.Add(new LineValueUpdate(name, value));
        }

        if (enabled.PlaytimeEnabled && sessionStart != 0)
        {
            var playtime = FormatPlaytime(GetSessionElapsed(sessionStart));
            if (MarkValueChanged("Session Playtime", playtime))
                updates.Add(new LineValueUpdate("Session Playtime", playtime));
        }

        if (updates.Count == 0)
            return;

        var version = Volatile.Read(ref _enabledStatsVersion);
        PostUi(() => ApplyFastTelemetryUpdate(version, updates.ToArray()));
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
            stats.Contains("CPU Frequency (per core)"),
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

    private bool MarkValueChanged(string label, string value)
    {
        lock (_lineCacheSync)
        {
            if (_lastPostedValues.TryGetValue(label, out var previous) &&
                string.Equals(previous, value, StringComparison.Ordinal))
                return false;

            _lastPostedValues[label] = value;
            return true;
        }
    }

    private void ApplyTelemetryUpdate(TelemetryUiUpdate update)
    {
        if (_disposed || update.Version != Volatile.Read(ref _enabledStatsVersion))
            return;

        _lastUiGame = update.Game;
        _hasLastUiGame = true;
        SyncLines(Volatile.Read(ref _enabledStats).Names);

        foreach (var lineUpdate in update.Values)
        {
            if (_lineByLabel.TryGetValue(lineUpdate.Label, out var line) && line.Value != lineUpdate.Value)
                line.Value = lineUpdate.Value;
        }

        if (update.ShowOverlay)
        {
            _window ??= new OverlayWindow { DataContext = _vm };
            if (!_window.IsVisible)
                _window.Show();
            ApplyOverlayPlacement(update.Game);
        }
        else
        {
            HideOverlayOnly();
        }

        UpdateFastLoopState();
    }

    private void ApplyFastTelemetryUpdate(int version, IReadOnlyList<LineValueUpdate> updates)
    {
        if (_disposed || version != Volatile.Read(ref _enabledStatsVersion))
            return;

        foreach (var item in updates)
        {
            if (_lineByLabel.TryGetValue(item.Label, out var line) && line.Value != item.Value)
                line.Value = item.Value;
        }
    }

    private void SyncLines(IReadOnlyList<string> names)
    {
        if (_vm.Lines.Select(static l => l.Label).SequenceEqual(names, StringComparer.Ordinal))
            return;

        _vm.Lines.Clear();
        _lineByLabel.Clear();
        foreach (var name in names)
        {
            OverlayLine line = name == "CPU Frequency (per core)"
                ? new PerCoreOverlayLine(name)
                : new OverlayLine(name);
            _vm.Lines.Add(line);
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
        var active = _window is { IsVisible: true } && (enabled.FastPlan.HasAny || enabled.PlaytimeEnabled);
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
                ? string.Join(Environment.NewLine, values.Select((v, i) => $"Core {i}: {v:0}{unit}"))
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

            "CPU Frequency (per core)" => Cores(snapshot.CpuCoreFrequenciesMHz, " MHz"),
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
        try { Task.WaitAll([_slowTask, _fastTask], TimeSpan.FromSeconds(2)); } catch { }

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

    private readonly record struct TelemetryUiUpdate(
        int Version,
        GameInfo Game,
        bool ShowOverlay,
        IReadOnlyList<LineValueUpdate> Values);

    private sealed class EnabledStatsSnapshot
    {
        public string[] Names { get; }
        public HashSet<string> Stats { get; }
        public bool PlaytimeEnabled { get; }
        public bool CpuFrequencyEnabled { get; }
        public PresentMonFastFramePlan FastPlan { get; }
        public PresentMonMetricPlan MetricPlan { get; }

        public EnabledStatsSnapshot(
            string[] names,
            HashSet<string> stats,
            bool playtimeEnabled,
            bool cpuFrequencyEnabled,
            PresentMonFastFramePlan fastPlan,
            PresentMonMetricPlan metricPlan)
        {
            Names = names;
            Stats = stats;
            PlaytimeEnabled = playtimeEnabled;
            CpuFrequencyEnabled = cpuFrequencyEnabled;
            FastPlan = fastPlan;
            MetricPlan = metricPlan;
        }
    }
}
