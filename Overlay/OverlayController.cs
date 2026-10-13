using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Clockwork.Services;
using Clockwork.ViewModels;

namespace Clockwork.Overlay;

public sealed class OverlayController : IDisposable
{
    private const int DetectionIntervalMs = 500;
    private const int TelemetryIntervalMs = 100;
    private const int FrameDrainIntervalMs = 16;
    private const int DisplayIntervalMs = 100;

    private const int HiddenTelemetryDivisor = DetectionIntervalMs / TelemetryIntervalMs;
    private const int MaxTrackedRefreshMisses = 8;
    private const int MaxRememberedSessions = 16;
    private const int CandidateDebounceMs = 2000;

    private static readonly long DisplayIntervalTicks = Stopwatch.Frequency * DisplayIntervalMs / 1000;
    private static readonly long CandidateDebounceTicks = Stopwatch.Frequency * CandidateDebounceMs / 1000;
    private static readonly long TopmostReassertIntervalTicks = Stopwatch.Frequency * 2;

    private readonly MainWindowViewModel _settings;
    private readonly OverlayViewModel _vm = new();
    private readonly PresentMonMonitor _presentMon = new();
    private readonly GameDetector _detector = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _fastWake = new(0, 1);
    private readonly SemaphoreSlim _telemetryWake = new(0, 1);
    private readonly object _stateSync = new();
    private readonly object _presentMonSync = new();
    private readonly object _uiUpdateSync = new();
    private readonly object _detectionSync = new();
    private readonly Dictionary<string, OverlayLine> _lineByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingUiUpdates = new(StatRegistry.Count, StringComparer.Ordinal);
    private readonly Action _applyPendingUiUpdatesAction;

    private readonly Dictionary<(int Pid, long CreateTime), long> _sessionStarts = new();

    private readonly double?[] _values = new double?[StatRegistry.Count];
    private readonly double?[] _fastValues = new double?[StatRegistry.Count];
    private readonly List<LineValueUpdate> _normalUpdates = new(32);
    private readonly List<LineValueUpdate> _fastUpdates = new(16);

    private readonly Task _detectionTask;
    private readonly Task _telemetryTask;
    private readonly Task _fastTask;

    private EnabledSnapshot _enabled;
    private int _gamePid;
    private GameInfo? _trackedGame;
    private long _sessionStartTimestamp;
    private int _trackedRefreshMisses;
    private bool _overlayWanted;
    private int _hiddenTelemetryTicks;
    private bool _hasReceivedTelemetry;
    private bool _overlayVisibleByHotkey = true;

    private int _candidatePid;
    private long _candidateFirstSeenTicks;
    private long _lastTopmostReassertTicks;

    private OverlayWindow? _window;
    private GameInfo _lastUiGame;
    private bool _hasLastUiGame;
    private int _uiGamePid;
    private bool _uiWasShowing;
    private int _uiPostPending;
    private int _lastAppliedPositionX = int.MinValue;
    private int _lastAppliedPositionY = int.MinValue;

    private volatile bool _disposed;
    private bool _fastLoopActive;
    private bool _telemetryLoopActive;

    public OverlayController(MainWindowViewModel settings)
    {
        _settings = settings;
        _enabled = BuildEnabledSnapshot();
        _applyPendingUiUpdatesAction = ApplyPendingUiUpdates;
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        foreach (var section in _settings.Sections)
        {
            foreach (var option in section.Options)
                option.PropertyChanged += OnOptionPropertyChanged;
        }

        SyncLines(_enabled.Labels);
        _detectionTask = Task.Run(DetectionLoopAsync);
        _telemetryTask = Task.Run(TelemetryLoopAsync);
        _fastTask = Task.Run(FastTelemetryLoopAsync);
    }

    public bool IsOverlayVisibleByHotkey => _overlayVisibleByHotkey;

    public void NotifyPresentMonChanged() => _presentMon.RequestImmediateRetry();

    public void ToggleVisibility()
    {
        var visible = !Volatile.Read(ref _overlayVisibleByHotkey);
        Volatile.Write(ref _overlayVisibleByHotkey, visible);

        if (!visible)
        {
            // Hiding is instant and only needs the UI thread.
            Volatile.Write(ref _overlayWanted, false);
            PostUi(HideOverlayOnly);
            return;
        }

        // Showing needs a detection pass. Run it off the UI thread; it serialises with the
        // periodic detection loop through _detectionSync.
        _ = Task.Run(() =>
        {
            if (_disposed)
                return;

            try { UpdateDetection(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"[Clockwork] Hotkey detection failed: {ex}");
            }
        });
    }

    private void OnOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not OptionViewModel || e.PropertyName != nameof(OptionViewModel.IsOn) || _disposed)
            return;

        if (_settings.IsBatchUpdating)
            return;

        RebuildEnabledStats();
    }

    private void RebuildEnabledStats()
    {
        var snapshot = BuildEnabledSnapshot();
        Volatile.Write(ref _enabled, snapshot);

        PostUi(() =>
        {
            if (_disposed) return;
            SyncLines(snapshot.Labels);
            UpdateFastLoopState();
        });

        SignalFastLoop();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsBatchUpdating) && !_settings.IsBatchUpdating)
        {
            RebuildEnabledStats();
            return;
        }

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

    private Task DetectionLoopAsync() => RunPeriodicAsync("Detection", DetectionIntervalMs, UpdateDetection);

    private async Task TelemetryLoopAsync()
    {
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!Volatile.Read(ref _telemetryLoopActive))
                {
                    await _telemetryWake.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TelemetryIntervalMs));
                while (Volatile.Read(ref _telemetryLoopActive) &&
                       await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    try { UpdateTelemetry(); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Debug.WriteLine($"[Clockwork] Telemetry tick failed: {ex}");
                    }
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
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!Volatile.Read(ref _fastLoopActive))
                {
                    await _fastWake.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameDrainIntervalMs));
                long lastPublish = 0;

                while (Volatile.Read(ref _fastLoopActive) &&
                       await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    try { UpdateFastTelemetry(ref lastPublish); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Debug.WriteLine($"[Clockwork] Fast telemetry tick failed: {ex}");
                    }
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

    private async Task RunPeriodicAsync(string name, int intervalMs, Action tick)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try { tick(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[Clockwork] {name} tick failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clockwork] {name} loop stopped: {ex}");
        }
    }

    private void SignalFastLoop()
    {
        try { _fastWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void SignalTelemetryLoop()
    {
        try { _telemetryWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void UpdateDetection()
    {
        // The game detector, its native process snapshot buffer and the candidate debounce state
        // are not thread-safe, so only one detection pass may run at a time.
        lock (_detectionSync)
            UpdateDetectionCore();
    }

    private void UpdateDetectionCore()
    {
        var enabled = Volatile.Read(ref _enabled);

        if (enabled.Count == 0 || !TryGetTrackedGame(enabled, out var game))
        {
            EndSession();
            Volatile.Write(ref _overlayWanted, false);
            if (_uiWasShowing || _window is { IsVisible: true })
            {
                PostUi(() =>
                {
                    HideOverlayOnly();
                    UpdateFastLoopState();
                });
            }
            return;
        }

        lock (_stateSync)
        {
            if (game.Pid != _gamePid)
            {
                Volatile.Write(ref _gamePid, game.Pid);
                _trackedRefreshMisses = 0;
                _sessionStartTimestamp = GetOrCreateSessionStart(game);
                Volatile.Write(ref _hasReceivedTelemetry, false);
            }

            _trackedGame = game;
        }

        UpdateTelemetryLoopState();

        var telemetryReady = enabled.PresentMonAll.IsEmpty || Volatile.Read(ref _hasReceivedTelemetry);
        var showOverlay = game.IsForeground &&
                          game.IsWindowVisible &&
                          !game.IsMinimized &&
                          telemetryReady &&
                          Volatile.Read(ref _overlayVisibleByHotkey);

        Volatile.Write(ref _overlayWanted, showOverlay);
        PostUi(() => ApplyDetectionUpdate(enabled, game, showOverlay));
    }

    private bool TryGetTrackedGame(EnabledSnapshot enabled, out GameInfo game)
    {
        var needFreshMemory = enabled.NeedsProcessRam;
        var now = Stopwatch.GetTimestamp();

        GameInfo currentTracked;
        bool hasTracked;
        lock (_stateSync)
        {
            hasTracked = _trackedGame.HasValue;
            currentTracked = _trackedGame ?? default;
        }

        if (_detector.TryGet(_settings.IncludeWindowedGames, needFreshMemory, out var foregroundGame))
        {
            if (hasTracked && foregroundGame.Pid == currentTracked.Pid)
            {
                _candidatePid = 0;
                _candidateFirstSeenTicks = 0;
                lock (_stateSync)
                    _trackedRefreshMisses = 0;

                game = foregroundGame;
                return true;
            }

            if (_candidatePid != foregroundGame.Pid)
            {
                _candidatePid = foregroundGame.Pid;
                _candidateFirstSeenTicks = now;
            }

            var candidateDuration = now - _candidateFirstSeenTicks;
            if (!hasTracked || candidateDuration >= CandidateDebounceTicks)
            {
                _candidatePid = 0;
                _candidateFirstSeenTicks = 0;
                lock (_stateSync)
                    _trackedRefreshMisses = 0;

                game = foregroundGame;
                return true;
            }

            if (hasTracked && !GameDetector.IsExecutableBlacklisted(currentTracked.Name))
            {
                if (_detector.TryRefreshTracked(currentTracked, needFreshMemory, out var backgroundRefreshed))
                {
                    lock (_stateSync)
                        _trackedRefreshMisses = 0;

                    game = backgroundRefreshed with { IsForeground = false };
                    return true;
                }
            }
        }
        else
        {
            _candidatePid = 0;
            _candidateFirstSeenTicks = 0;
        }

        if (!hasTracked)
        {
            game = default;
            return false;
        }

        if (GameDetector.IsExecutableBlacklisted(currentTracked.Name))
        {
            game = default;
            return false;
        }

        if (_detector.TryRefreshTracked(currentTracked, needFreshMemory, out var refreshed))
        {
            lock (_stateSync)
                _trackedRefreshMisses = 0;

            game = refreshed with { IsForeground = false };
            return true;
        }

        int misses;
        lock (_stateSync)
            misses = ++_trackedRefreshMisses;

        if (misses > MaxTrackedRefreshMisses)
        {
            game = default;
            return false;
        }

        if (!GameDetector.TryRefreshTrackedWindowOnly(currentTracked, out var windowRefreshed))
        {
            windowRefreshed = currentTracked with
            {
                IsForeground = false,
                IsWindowVisible = false,
                IsMinimized = true,
            };
        }
        else
        {
            windowRefreshed = windowRefreshed with { IsForeground = false };
        }

        game = windowRefreshed;
        return true;
    }

    private long GetOrCreateSessionStart(GameInfo game)
    {
        var key = (game.Pid, game.CreateTime);
        if (_sessionStarts.TryGetValue(key, out var existing))
            return existing;

        if (_sessionStarts.Count >= MaxRememberedSessions)
        {
            var oldestKey = default((int Pid, long CreateTime));
            var oldest = long.MaxValue;
            foreach (var pair in _sessionStarts)
            {
                if (pair.Value < oldest)
                {
                    oldest = pair.Value;
                    oldestKey = pair.Key;
                }
            }

            _sessionStarts.Remove(oldestKey);
        }

        var now = Stopwatch.GetTimestamp();
        _sessionStarts[key] = now;
        return now;
    }

    private void EndSession()
    {
        lock (_stateSync)
        {
            if (_trackedGame is { } ended)
                _sessionStarts.Remove((ended.Pid, ended.CreateTime));

            Volatile.Write(ref _gamePid, 0);
            _trackedGame = null;
            _trackedRefreshMisses = 0;
            _sessionStartTimestamp = 0;
            Volatile.Write(ref _hasReceivedTelemetry, false);
        }

        UpdateTelemetryLoopState();

        lock (_presentMonSync)
        {
            if (_presentMon.IsTracking)
                _presentMon.StopTracking();
        }
    }

    private long GetSessionElapsedSeconds()
    {
        long start;
        lock (_stateSync)
            start = _sessionStartTimestamp;

        if (start == 0)
            return 0;

        var elapsedTicks = Stopwatch.GetTimestamp() - start;
        return Math.Max(0, elapsedTicks) / Stopwatch.Frequency;
    }

    private void UpdateTelemetry()
    {
        var enabled = Volatile.Read(ref _enabled);
        if (enabled.Count == 0)
            return;

        GameInfo game;
        lock (_stateSync)
        {
            if (_trackedGame is not { } tracked)
                return;

            game = tracked;
        }

        if (!Volatile.Read(ref _overlayWanted) && ++_hiddenTelemetryTicks < HiddenTelemetryDivisor)
            return;

        _hiddenTelemetryTicks = 0;

        lock (_presentMonSync)
        {
            if (Volatile.Read(ref _gamePid) != game.Pid)
                return;

            if (!enabled.PresentMonAll.IsEmpty)
            {
                _presentMon.StartTracking(game.Pid, enabled.PresentMonAll);
                _presentMon.Update();
            }
            else if (_presentMon.IsTracking)
            {
                _presentMon.StopTracking();
            }
        }

        // Telemetry counts as "received" as soon as any enabled PresentMon statistic has a value,
        // fast-owned ones included. The fast loop only runs once the overlay is visible, so it
        // cannot be what makes the overlay appear in the first place.
        if (!enabled.PresentMonAll.IsEmpty &&
            !Volatile.Read(ref _hasReceivedTelemetry) &&
            _presentMon.HasAnyValue(enabled.PresentMonAll))
        {
            Volatile.Write(ref _hasReceivedTelemetry, true);
        }

        if (enabled.NormalSlots.Length == 0)
            return;

        if (!enabled.PresentMonNormal.IsEmpty)
            _presentMon.FillSnapshot(enabled.PresentMonNormal, _values);

        var ram = enabled.NeedsRam ? Win32.GetRamUsage() : null;
        var now = DateTime.Now;
        var elapsedSeconds = GetSessionElapsedSeconds();

        _normalUpdates.Clear();
        foreach (var slot in enabled.NormalSlots)
        {
            if (TryUpdateNormalSlot(slot, game, ram, now, elapsedSeconds, out var text))
                _normalUpdates.Add(new LineValueUpdate(slot.Label, text));
        }

        if (_normalUpdates.Count == 0)
            return;

        lock (_uiUpdateSync)
        {
            for (var i = 0; i < _normalUpdates.Count; i++)
            {
                var item = _normalUpdates[i];
                _pendingUiUpdates[item.Label] = item.Value;
            }
        }

        ScheduleUiUpdates();
    }

    private bool TryUpdateNormalSlot(
        StatSlot slot,
        GameInfo game,
        (double usedGb, double totalGb, double percent)? ram,
        DateTime now,
        long elapsedSeconds,
        out string text)
    {
        var definition = slot.Definition;
        switch (definition.Kind)
        {
            case StatKind.Number:
                {
                    var value = _values[(int)definition.Id];
                    return slot.TryUpdateNumber(value, StatusFor(definition.Id, value), out text);
                }
            case StatKind.Flag:
                {
                    var value = _values[(int)definition.Id];
                    return slot.TryUpdateFlag(value, StatusFor(definition.Id, value), out text);
                }
            case StatKind.Ram:
                return slot.TryUpdateText(
                    ram is { } memory
                        ? OverlayStatFormatter.FormatRam(memory.usedGb, memory.totalGb)
                        : OverlayStatFormatter.NotAvailable,
                    out text);
            case StatKind.RamPercent:
                return ram is { } usage
                    ? slot.TryUpdateKeyed((long)Math.Round(usage.percent), OverlayStatFormatter.FormatPercent, out text)
                    : slot.TryUpdateText(OverlayStatFormatter.NotAvailable, out text);
            case StatKind.ProcessRam:
                return game.PrivateWorkingSetBytes is { } bytes
                    ? slot.TryUpdateKeyed((long)Math.Round(bytes / 107374182.4), OverlayStatFormatter.FormatTenthsOfGb, out text)
                    : slot.TryUpdateText(OverlayStatFormatter.NotAvailable, out text);
            case StatKind.SystemTime:
                return slot.TryUpdateKeyed(now.Hour * 60L + now.Minute, OverlayStatFormatter.FormatClock, out text);
            case StatKind.Playtime:
                return slot.TryUpdateKeyed(elapsedSeconds, OverlayStatFormatter.FormatPlaytime, out text);
            default:
                return slot.TryUpdateText(OverlayStatFormatter.NotAvailable, out text);
        }
    }

    private string? StatusFor(StatId id, double? value) =>
        value is { } v && double.IsFinite(v) ? null : _presentMon.GetStatusText(id);

    private void UpdateFastTelemetry(ref long lastPublish)
    {
        var enabled = Volatile.Read(ref _enabled);
        if (enabled.PresentMonFast.IsEmpty)
            return;

        GameInfo game;
        lock (_stateSync)
        {
            if (_trackedGame is not { } tracked)
                return;

            game = tracked;
        }

        lock (_presentMonSync)
        {
            if (Volatile.Read(ref _gamePid) != game.Pid || _presentMon.TrackedPid != game.Pid)
                return;

            _presentMon.UpdateFastFrameMetrics();
        }

        var now = Stopwatch.GetTimestamp();
        if (now - lastPublish < DisplayIntervalTicks)
            return;

        lastPublish = now;
        _presentMon.FillSnapshot(enabled.PresentMonFast, _fastValues);

        _fastUpdates.Clear();
        foreach (var slot in enabled.FastSlots)
        {
            if (slot.TryUpdateNumber(_fastValues[(int)slot.Definition.Id], null, out var text))
                _fastUpdates.Add(new LineValueUpdate(slot.Label, text));
        }

        if (_fastUpdates.Count == 0)
            return;

        lock (_uiUpdateSync)
        {
            for (var i = 0; i < _fastUpdates.Count; i++)
            {
                var item = _fastUpdates[i];
                _pendingUiUpdates[item.Label] = item.Value;
            }
        }

        ScheduleUiUpdates();
    }

    private EnabledSnapshot BuildEnabledSnapshot()
    {
        var slots = new List<StatSlot>();
        foreach (var section in _settings.Sections)
        {
            foreach (var option in section.Options)
            {
                if (option.IsOn)
                    slots.Add(new StatSlot(option.Stat));
            }
        }

        return new EnabledSnapshot(slots.ToArray());
    }

    private void PostUi(Action action)
    {
        if (_disposed)
            return;

        Dispatcher.UIThread.Post(action);
    }

    private void ScheduleUiUpdates()
    {
        if (_disposed)
            return;

        if (Interlocked.CompareExchange(ref _uiPostPending, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(_applyPendingUiUpdatesAction, DispatcherPriority.Background);
        }
    }

    private void ApplyPendingUiUpdates()
    {
        Interlocked.Exchange(ref _uiPostPending, 0);
        if (_disposed)
            return;

        var enabled = Volatile.Read(ref _enabled);
        SyncLines(enabled.Labels);

        lock (_uiUpdateSync)
        {
            foreach (var pair in _pendingUiUpdates)
            {
                if (_lineByLabel.TryGetValue(pair.Key, out var line) && line.Value != pair.Value)
                    line.Value = pair.Value;
            }
            _pendingUiUpdates.Clear();
        }
    }

    private void ApplyDetectionUpdate(EnabledSnapshot enabled, GameInfo game, bool showOverlay)
    {
        if (_disposed || !ReferenceEquals(enabled, Volatile.Read(ref _enabled)))
            return;

        // A detection pass that started before the hotkey hid the overlay must not re-show it.
        if (showOverlay && !Volatile.Read(ref _overlayVisibleByHotkey))
            showOverlay = false;

        if (game.Pid != _uiGamePid)
        {
            _uiGamePid = game.Pid;
            _lastAppliedPositionX = int.MinValue;
            _lastAppliedPositionY = int.MinValue;
        }

        _lastUiGame = game;
        _hasLastUiGame = true;
        SyncLines(enabled.Labels);

        if (showOverlay)
        {
            if (_window is null)
            {
                _window = new OverlayWindow { DataContext = _vm };
                _window.SizeChanged += OnOverlayWindowSizeChanged;
            }

            var justShown = !_window.IsVisible;
            if (justShown)
                _window.Show();

            ApplyOverlayPlacement(game);

            var nowTicks = Stopwatch.GetTimestamp();
            if (justShown || !_uiWasShowing || nowTicks - _lastTopmostReassertTicks >= TopmostReassertIntervalTicks)
            {
                _window.EnsureTopmost();
                _lastTopmostReassertTicks = nowTicks;
            }

            _uiWasShowing = true;
        }
        else
        {
            HideOverlayOnly();
        }

        UpdateFastLoopState();
    }

    private void OnOverlayWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_disposed || !_hasLastUiGame || _window is not { IsVisible: true })
            return;

        _lastAppliedPositionX = int.MinValue;
        _lastAppliedPositionY = int.MinValue;
        ApplyOverlayPlacement(_lastUiGame);
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

            OverlayLine line = new(name);
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

        if (_window.Bounds.Width <= 0 || _window.Bounds.Height <= 0)
        {
            _window.Position = new PixelPoint(game.Monitor.Left + 16, game.Monitor.Top + 16);
            return;
        }

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
        var enabled = Volatile.Read(ref _enabled);
        var active = _window is { IsVisible: true } && !enabled.PresentMonFast.IsEmpty;
        Volatile.Write(ref _fastLoopActive, active);
        if (active)
            SignalFastLoop();
    }

    private void UpdateTelemetryLoopState()
    {
        var active = Volatile.Read(ref _gamePid) != 0;
        Volatile.Write(ref _telemetryLoopActive, active);
        if (active)
            SignalTelemetryLoop();
    }

    private void HideOverlayOnly()
    {
        if (_window is { IsVisible: true })
            _window.Hide();

        _uiWasShowing = false;
        Volatile.Write(ref _fastLoopActive, false);
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
        SignalFastLoop();
        SignalTelemetryLoop();

        try { Task.WaitAll([_detectionTask, _telemetryTask, _fastTask], TimeSpan.FromMilliseconds(500)); }
        catch { }

        if (_window is not null)
        {
            _window.SizeChanged -= OnOverlayWindowSizeChanged;
            _window.Close();
            _window = null;
        }

        EndSession();
        lock (_presentMonSync)
            _presentMon.Dispose();
        _detector.Dispose();
        _fastWake.Dispose();
        _telemetryWake.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly record struct LineValueUpdate(string Label, string Value);

    private sealed class EnabledSnapshot
    {
        public EnabledSnapshot(StatSlot[] slots)
        {
            var labels = new string[slots.Length];
            var normal = new List<StatSlot>(slots.Length);
            var fast = new List<StatSlot>(slots.Length);
            var stats = StatSet.Empty;

            for (var i = 0; i < slots.Length; i++)
            {
                var definition = slots[i].Definition;
                labels[i] = definition.Label;
                stats = stats.With(definition.Id);

                if (StatGroups.FastOwned.Contains(definition.Id))
                    fast.Add(slots[i]);
                else
                    normal.Add(slots[i]);
            }

            Labels = labels;
            Stats = stats;
            NormalSlots = normal.ToArray();
            FastSlots = fast.ToArray();
            PresentMonAll = stats & StatGroups.PresentMonBacked;
            PresentMonFast = PresentMonAll & StatGroups.FastOwned;
            PresentMonNormal = PresentMonAll - StatGroups.FastOwned;
            NeedsRam = stats.Contains(StatId.RamUsage) || stats.Contains(StatId.RamPercent);
            NeedsProcessRam = stats.Contains(StatId.ProcessRam);
        }

        public string[] Labels { get; }
        public StatSet Stats { get; }
        public StatSlot[] NormalSlots { get; }
        public StatSlot[] FastSlots { get; }
        public StatSet PresentMonAll { get; }
        public StatSet PresentMonFast { get; }
        public StatSet PresentMonNormal { get; }
        public bool NeedsRam { get; }
        public bool NeedsProcessRam { get; }
        public int Count => Labels.Length;
    }
}