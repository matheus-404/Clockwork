using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Clockwork.Overlay;

namespace Clockwork.Services;

/// <summary>
/// PresentMon Service client used by Clockwork for FPS, CPU, and GPU telemetry.
/// RAM usage remains in Win32 because it is a Windows system-memory statistic rather than a
/// PresentMon hardware metric.
/// </summary>
/// <remarks>
/// Threading: <see cref="StartTracking"/>, <see cref="StopTracking"/>, <see cref="Update"/> and
/// <see cref="UpdateFastFrameMetrics"/> talk to the native API and must be serialised by the
/// caller. <see cref="FillSnapshot"/> and <see cref="GetStatusText"/> only read managed data under
/// an internal lock and may be called from any thread without that serialisation.
/// </remarks>
public sealed class PresentMonMonitor : IDisposable
{
    private const int MaxSwapChains = 64;
    private const uint TelemetryPollingPeriodMs = 100;
    private const uint EtwFlushPeriodMs = 16;
    private const int FrameBufferCapacity = 4096;
    private const int ConnectionErrorsBeforeReconnect = 20;
    private const double MaxPlausibleFrameMs = 10_000;

    /// <summary>
    /// Averaging window of the polled (dynamic) query. CPU and GPU readings are this many
    /// milliseconds wide moving averages: lower is more responsive but noisier, higher is smoother.
    /// </summary>
    internal const double DynamicWindowMs = 250.0;

    /// <summary>How long the lows window looks back.</summary>
    internal const double LowsWindowSeconds = 60.0;

    private static readonly long ValueExpiryTicks = Stopwatch.Frequency * 2;
    private static readonly long LowsRefreshTicks = Stopwatch.Frequency;

    private readonly object _sync = new();

    // Frame history. Guarded by _sync.
    private readonly FrameStatistics _frames = new();
    private readonly double[] _frameScratch = new double[FrameBufferCapacity];
    private readonly double[] _lowsScratch = new double[65_536];
    private long _lowsComputedAt;
    private double? _low1;
    private double? _low01;

    // Native state. Touched only by the caller-serialised native methods.
    private PresentMonNative.Api? _api;
    private IntPtr _session;
    private IntPtr _dynamicQuery;
    private IntPtr _frameQuery;
    private IntPtr _dynamicBlob;
    private IntPtr _frameBlob;
    private uint _dynamicBlobSize;
    private uint _frameBlobSize;
    private uint _dynamicBlobCapacity;
    private string? _dynamicSignature;
    private string? _frameSignature;
    private bool _processTracked;
    private bool _queriesDirty;
    private bool _introspectionValid;
    private StatSet _stats;
    private int _trackedPid;
    private string? _lastError;

    // Retry/backoff state.
    private long _nextConnectAttemptTs;
    private long _nextQueryAttemptTs;
    private int _connectFailures;
    private int _queryFailures;
    private int _consecutiveConnectionErrors;

    // Introspection data. Guarded by _sync.
    private Dictionary<PresentMonNative.PM_METRIC, MetricInfo> _metrics = new();
    private readonly Dictionary<uint, PresentMonNative.PM_DEVICE_TYPE> _deviceTypes = new();
    private readonly List<uint> _graphicsDeviceIds = new();
    private uint _systemDeviceId;
    private bool _hasSystemDevice;
    private uint _independentDeviceId;
    private bool _hasIndependentDevice;

    // Query bindings and the latest values. Guarded by _sync.
    private readonly List<Binding> _bindings = new();
    private readonly List<Binding> _frameBindings = new();
    private readonly Dictionary<uint, GpuSample> _gpuSamples = new();
    private long _gpuSamplesAt;
    private readonly TimedValue[] _values = new TimedValue[(int)MetricKey.Count];
    private ulong _polledMask;

    public bool IsAvailable => _session != 0 && _api is not null;

    public bool IsTracking => Volatile.Read(ref _trackedPid) != 0;

    /// <summary>The process currently being tracked, or 0.</summary>
    public int TrackedPid => Volatile.Read(ref _trackedPid);

    public string? LastError => _lastError;

    /// <summary>Makes the next update retry connecting right away instead of waiting out a backoff.</summary>
    public void RequestImmediateRetry()
    {
        Volatile.Write(ref _nextConnectAttemptTs, 0);
        Volatile.Write(ref _nextQueryAttemptTs, 0);
    }

    public void StartTracking(int pid, StatSet stats)
    {
        stats &= StatGroups.PresentMonBacked;
        if (pid <= 0 || stats.IsEmpty)
        {
            StopTracking();
            return;
        }

        if (TrackedPid != pid)
        {
            StopTracking();
            Volatile.Write(ref _trackedPid, pid);
        }

        if (stats != _stats)
        {
            // A different set of statistics only rebuilds queries whose contents actually changed
            // (see EnsureQueries) and leaves frame history, the running average and every value
            // that is still wanted untouched.
            _stats = stats;
            _queriesDirty = true;
            lock (_sync)
                _frames.SetAverageActive(stats.Contains(StatId.AvgFps));
        }

        if (!_processTracked)
            TryConnect();

        if (_processTracked && _queriesDirty)
            EnsureQueries();
    }

    public void StopTracking()
    {
        var api = _api;
        var pid = TrackedPid;
        if (api is not null && _session != 0 && _processTracked && pid != 0)
        {
            try { api.StopTrackingProcess(_session, (uint)pid); } catch { }
        }

        FreeQueries();
        _processTracked = false;
        _queriesDirty = false;
        _introspectionValid = false;
        _stats = StatSet.Empty;
        Volatile.Write(ref _trackedPid, 0);

        lock (_sync)
        {
            _frames.Reset();
            Array.Clear(_values);
            _polledMask = 0;
            _gpuSamples.Clear();
            _gpuSamplesAt = 0;
            _low1 = null;
            _low01 = null;
            _lowsComputedAt = 0;
        }

        _connectFailures = 0;
        _queryFailures = 0;
        _consecutiveConnectionErrors = 0;
        Volatile.Write(ref _nextConnectAttemptTs, 0);
        Volatile.Write(ref _nextQueryAttemptTs, 0);
    }

    public void Update()
    {
        if (TrackedPid == 0)
            return;

        if (!_processTracked)
        {
            TryConnect();
            if (!_processTracked)
                return;
        }

        if (_queriesDirty)
            EnsureQueries();

        if (_dynamicQuery == 0 && _frameQuery == 0)
            return;

        try
        {
            PollDynamicMetrics();
            ConsumeFrames();
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon telemetry error: {ex.Message}";
            FreeQueries();
            _queriesDirty = true;
            ScheduleQueryRetry();
        }
    }

    public void UpdateFastFrameMetrics()
    {
        if (TrackedPid == 0 || !IsAvailable || _frameQuery == 0 || _frameBlob == 0)
            return;

        try
        {
            ConsumeFrames();
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon fast frame telemetry error: {ex.Message}";
        }
    }

    /// <summary>
    /// Writes the current value of every statistic in <paramref name="wanted"/> into
    /// <paramref name="values"/> (indexed by <see cref="StatId"/>), using null for "no data".
    /// Safe to call without holding the caller's native-call lock; the (potentially slow) lows
    /// sort happens outside the internal lock as well.
    /// </summary>
    public void FillSnapshot(StatSet wanted, double?[] values)
    {
        wanted &= StatGroups.PresentMonBacked;
        if (wanted.IsEmpty)
            return;

        var now = Stopwatch.GetTimestamp();

        double? low1 = null;
        double? low01 = null;
        if (wanted.Contains(StatId.Low1Fps) || wanted.Contains(StatId.Low01Fps))
            (low1, low01) = GetLows(now);

        lock (_sync)
        {
            var gpu = wanted.Intersects(StatGroups.Gpu) ? ChooseActiveGpu(now) : null;
            for (var bits = wanted.Bits; bits != 0; bits &= bits - 1)
            {
                var id = (StatId)BitOperations.TrailingZeroCount(bits);
                values[(int)id] = ReadStat(id, now, gpu, low1, low01);
            }
        }
    }

    /// <summary>
    /// Explains why a statistic has no value ("Unsupported" or "Unavailable") for the few
    /// statistics where PresentMon reports that, or null when there is nothing to add.
    /// </summary>
    internal string? GetStatusText(StatId id)
    {
        var metric = id switch
        {
            StatId.GpuRenderCompute => PresentMonNative.PM_METRIC.GPU_RENDER_COMPUTE_UTILIZATION,
            StatId.GpuPowerLimited => PresentMonNative.PM_METRIC.GPU_POWER_LIMITED,
            StatId.GpuTemperatureLimited => PresentMonNative.PM_METRIC.GPU_TEMPERATURE_LIMITED,
            StatId.GpuCurrentLimited => PresentMonNative.PM_METRIC.GPU_CURRENT_LIMITED,
            StatId.GpuVoltageLimited => PresentMonNative.PM_METRIC.GPU_VOLTAGE_LIMITED,
            StatId.GpuUtilizationLimited => PresentMonNative.PM_METRIC.GPU_UTILIZATION_LIMITED,
            _ => (PresentMonNative.PM_METRIC?)null,
        };

        return metric is null ? null : GetMetricStatusText(metric.Value);
    }

    private string? GetMetricStatusText(PresentMonNative.PM_METRIC metric)
    {
        lock (_sync)
        {
            if (TrackedPid == 0 || !IsAvailable)
                return null;

            if (IsMetricBound(metric))
                return null;

            if (!_metrics.TryGetValue(metric, out var info))
                return "Unsupported";

            var anyAvailable = false;
            var anyUnsupported = false;
            var anyUnavailable = false;
            foreach (var availability in info.DeviceAvailability.Values)
            {
                switch (availability)
                {
                    case PresentMonNative.PM_METRIC_AVAILABILITY.AVAILABLE:
                        anyAvailable = true;
                        break;
                    case PresentMonNative.PM_METRIC_AVAILABILITY.NOT_EXPORTED_BY_SOURCE:
                    case PresentMonNative.PM_METRIC_AVAILABILITY.NOT_SUPPORTED_BY_DEVICE:
                    case PresentMonNative.PM_METRIC_AVAILABILITY.NOT_IMPLEMENTED_BY_PRESENTMON:
                        anyUnsupported = true;
                        break;
                    case PresentMonNative.PM_METRIC_AVAILABILITY.UNAVAILABLE:
                        anyUnavailable = true;
                        break;
                }
            }

            if (anyAvailable)
                return null;

            if (anyUnsupported)
                return "Unsupported";

            return anyUnavailable ? "Unavailable" : "Unsupported";
        }
    }

    // Callers hold _sync.
    private bool IsMetricBound(PresentMonNative.PM_METRIC metric)
    {
        foreach (var binding in _bindings)
        {
            if (binding.Element.metric == metric)
                return true;
        }

        foreach (var binding in _frameBindings)
        {
            if (binding.Element.metric == metric)
                return true;
        }

        return false;
    }

    // Callers hold _sync.
    private double? ReadStat(StatId id, long now, GpuSample? gpu, double? low1, double? low01) => id switch
    {
        StatId.Fps => _frames.CalculateFps(now),
        StatId.AvgFps => _frames.CalculateAverageFps(),
        StatId.Low1Fps => low1,
        StatId.Low01Fps => low01,
        StatId.FrameTime => _frames.CalculateFrameTime(now),
        StatId.DroppedFrames => GetValue(MetricKey.DroppedFrames, now),
        StatId.PresentedFps => GetRateMetric(MetricKey.PresentedFps, now, fallbackFromFrameTimes: true),
        StatId.DisplayedFps => GetRateMetric(MetricKey.DisplayedFps, now, fallbackFromDisplayedTime: true),
        StatId.ApplicationFps => GetRateMetric(MetricKey.ApplicationFps, now, fallbackFromFrameTimes: true),

        StatId.CpuUsage => GetValue(MetricKey.CpuUsage, now),
        StatId.CpuBusy => GetValue(MetricKey.CpuBusy, now),
        StatId.CpuWait => GetValue(MetricKey.CpuWait, now),
        StatId.CpuFrameTime => GetValue(MetricKey.CpuFrameTime, now),

        StatId.GpuTemperature => gpu?.TemperatureC,
        StatId.GpuCoreClock => gpu?.CoreClockMHz,
        StatId.GpuMemoryClock => gpu?.MemoryClockMHz,
        StatId.GpuVramUsage => gpu?.VramUsedMb,
        StatId.GpuVramPercent => gpu?.VramUsagePercent,
        StatId.GpuPower => gpu?.PowerW,
        StatId.GpuUsage => gpu?.UsagePercent,
        StatId.GpuRenderCompute => gpu?.RenderComputeUsagePercent,
        StatId.GpuPowerLimited => gpu?.PowerLimited,
        StatId.GpuTemperatureLimited => gpu?.TemperatureLimited,
        StatId.GpuCurrentLimited => gpu?.CurrentLimited,
        StatId.GpuVoltageLimited => gpu?.VoltageLimited,
        StatId.GpuUtilizationLimited => gpu?.UtilizationLimited,
        StatId.GpuBusy => GetValue(MetricKey.GpuBusy, now),
        StatId.GpuWait => GetValue(MetricKey.GpuWait, now),
        StatId.GpuTime => GetValue(MetricKey.GpuTime, now),

        StatId.GpuLatency => GetValue(MetricKey.GpuLatency, now),
        StatId.DisplayLatency => GetValue(MetricKey.DisplayLatency, now),
        StatId.RenderPresentLatency => GetValue(MetricKey.RenderPresentLatency, now),
        StatId.UntilDisplayed => GetValue(MetricKey.UntilDisplayed, now),
        StatId.BetweenPresents => GetValue(MetricKey.BetweenPresents, now),
        StatId.BetweenDisplayChanges => GetValue(MetricKey.BetweenDisplayChange, now),
        StatId.ClickToPhotonLatency => GetValue(MetricKey.ClickToPhotonLatency, now),
        StatId.AllInputToPhotonLatency => GetValue(MetricKey.AllInputToPhotonLatency, now),
        _ => null,
    };

    private (double? Low1, double? Low01) GetLows(long now)
    {
        int count;
        lock (_sync)
        {
            if (_lowsComputedAt != 0 && now - _lowsComputedAt < LowsRefreshTicks)
                return (_low1, _low01);

            count = _frames.CopyRecentFrameTimes(now, LowsWindowSeconds, _lowsScratch);
        }

        // The sort runs outside every lock, so it can never hold up frame consumption.
        var result = count > 0 ? FrameStatistics.ComputeLows(_lowsScratch, count) : (null, null);

        lock (_sync)
        {
            _low1 = result.Item1;
            _low01 = result.Item2;
            _lowsComputedAt = now;
        }

        return result;
    }

    // ---- connection management ---------------------------------------------------------------

    private void TryConnect()
    {
        if (Stopwatch.GetTimestamp() < Volatile.Read(ref _nextConnectAttemptTs))
            return;

        var pid = TrackedPid;
        if (pid == 0)
            return;

        try
        {
            if (!EnsureSession() || _api is null)
            {
                ScheduleConnectRetry();
                return;
            }

            var api = _api;
            var status = api.StartTrackingProcess(_session, (uint)pid);
            if (status != PresentMonNative.PM_STATUS.SUCCESS &&
                status != PresentMonNative.PM_STATUS.ALREADY_TRACKING_PROCESS)
            {
                _lastError = $"PresentMon could not track PID {pid}: {PresentMonNative.StatusText(status)}";
                ScheduleConnectRetry();
                return;
            }

            api.SetTelemetryPollingPeriod(_session, 0u, TelemetryPollingPeriodMs);
            api.SetEtwFlushPeriod(_session, EtwFlushPeriodMs);

            _processTracked = true;
            _queriesDirty = true;
            _connectFailures = 0;
            _consecutiveConnectionErrors = 0;
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon initialization error: {ex.Message}";
            DropSession();
            ScheduleConnectRetry();
        }
    }

    private bool EnsureSession()
    {
        if (_session != 0 && _api is not null)
            return true;

        if (!PresentMonNative.Api.TryLoad(out var api, out var loadError) || api is null)
        {
            _lastError = loadError ?? "Failed to load PresentMon API.";
            return false;
        }

        var versionStatus = api.GetApiVersion(out var version);
        if (versionStatus != PresentMonNative.PM_STATUS.SUCCESS)
        {
            _lastError = $"PresentMon API version query failed: {PresentMonNative.StatusText(versionStatus)}";
            api.Dispose();
            return false;
        }

        if (version.major != 3)
        {
            _lastError = $"Unsupported PresentMon API version {version.major}.{version.minor}.{version.patch}. Clockwork requires API major version 3.";
            api.Dispose();
            return false;
        }

        var status = api.OpenSession(out _session);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            _lastError = $"PresentMon service session could not be opened: {PresentMonNative.StatusText(status)}";
            _session = 0;
            api.Dispose();
            return false;
        }

        _api = api;
        _lastError = null;
        return true;
    }

    /// <summary>Closes the session so the next connection attempt starts from scratch.</summary>
    private void DropSession()
    {
        FreeQueries();
        _processTracked = false;
        _queriesDirty = false;
        _introspectionValid = false;
        _consecutiveConnectionErrors = 0;

        var api = _api;
        if (api is not null && _session != 0)
        {
            try { api.CloseSession(_session); } catch { }
        }

        _session = 0;
        api?.Dispose();
        _api = null;
    }

    private void ScheduleConnectRetry()
    {
        _connectFailures++;
        var seconds = _connectFailures <= 1 ? 3 : 10;
        Volatile.Write(ref _nextConnectAttemptTs, Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds);
    }

    private void ScheduleQueryRetry()
    {
        _queryFailures++;
        var seconds = _queryFailures <= 1 ? 3 : 10;
        Volatile.Write(ref _nextQueryAttemptTs, Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds);
    }

    private static bool IsConnectionStatus(PresentMonNative.PM_STATUS status) =>
        status is PresentMonNative.PM_STATUS.SERVICE_ERROR
            or PresentMonNative.PM_STATUS.PIPE_ERROR
            or PresentMonNative.PM_STATUS.SESSION_NOT_OPEN
            or PresentMonNative.PM_STATUS.BAD_HANDLE;

    /// <summary>
    /// Counts consecutive connection-type failures. After a sustained run the session is dropped
    /// (for example because the PresentMon service restarted) so it can be re-opened with backoff.
    /// Returns true when the session was dropped.
    /// </summary>
    private bool NoteStatus(PresentMonNative.PM_STATUS status)
    {
        if (status == PresentMonNative.PM_STATUS.SUCCESS)
        {
            _consecutiveConnectionErrors = 0;
            return false;
        }

        if (!IsConnectionStatus(status))
            return false;

        if (++_consecutiveConnectionErrors < ConnectionErrorsBeforeReconnect)
            return false;

        _lastError = $"PresentMon connection lost: {PresentMonNative.StatusText(status)}";
        DropSession();
        ScheduleConnectRetry();
        return true;
    }

    // ---- query management --------------------------------------------------------------------

    private void EnsureQueries()
    {
        if (!_queriesDirty)
            return;

        if (Stopwatch.GetTimestamp() < Volatile.Read(ref _nextQueryAttemptTs))
            return;

        var api = _api;
        if (api is null || _session == 0 || !_processTracked)
            return;

        try
        {
            // Introspection (devices, metric availability) does not change while a process is
            // tracked, so it is read once per tracking session instead of on every plan change.
            if (!_introspectionValid && !LoadIntrospection(api))
            {
                ScheduleQueryRetry();
                return;
            }

            var dynamicOk = ApplyDynamicQuery(api, CollectDynamicSpecs(_stats));
            var frameOk = ApplyFrameQuery(api, CollectFrameSpecs(_stats));
            if (dynamicOk && frameOk)
            {
                _queriesDirty = false;
                _queryFailures = 0;
            }
            else
            {
                ScheduleQueryRetry();
            }
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon query setup error: {ex.Message}";
            FreeQueries();
            ScheduleQueryRetry();
        }
    }

    private bool LoadIntrospection(PresentMonNative.Api api)
    {
        var rootStatus = api.GetIntrospectionRoot(_session, out var rootPtr);
        if (rootStatus != PresentMonNative.PM_STATUS.SUCCESS || rootPtr == 0)
        {
            _lastError = $"PresentMon introspection failed: {PresentMonNative.StatusText(rootStatus)}";
            return false;
        }

        try
        {
            lock (_sync)
                ReadIntrospection(rootPtr);

            _introspectionValid = true;
            return true;
        }
        finally
        {
            try { api.FreeIntrospectionRoot(rootPtr); } catch { }
        }
    }

    private bool ApplyDynamicQuery(PresentMonNative.Api api, List<QuerySpec> specs)
    {
        var signature = BuildSignature(specs);
        if (signature == _dynamicSignature && (specs.Count == 0 || _dynamicQuery != 0))
            return true;

        FreeDynamicQuery();
        _dynamicSignature = null;

        if (specs.Count == 0)
        {
            _dynamicSignature = signature;
            return true;
        }

        var elements = new PresentMonNative.PM_QUERY_ELEMENT[specs.Count];
        for (var i = 0; i < specs.Count; i++)
            elements[i] = specs[i].Element;

        var status = api.RegisterDynamicQuery(_session, out var query, elements, (ulong)elements.Length, DynamicWindowMs, 0u);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            _lastError = $"PresentMon dynamic query failed: {PresentMonNative.StatusText(status)}";
            return false;
        }

        _dynamicQuery = query;

        lock (_sync)
        {
            _bindings.Clear();
            for (var i = 0; i < specs.Count; i++)
            {
                var binding = specs[i].Binding;
                binding.Element = elements[i];
                _bindings.Add(binding);
            }
        }

        var size = 0UL;
        foreach (var element in elements)
            size = Math.Max(size, element.dataOffset + element.dataSize);

        _dynamicBlobSize = (uint)Math.Min(uint.MaxValue, size);
        _dynamicBlobCapacity = Math.Max(4096u, checked(_dynamicBlobSize * (uint)MaxSwapChains));
        _dynamicBlob = Marshal.AllocHGlobal((nint)_dynamicBlobCapacity);
        _dynamicSignature = signature;
        return true;
    }

    private bool ApplyFrameQuery(PresentMonNative.Api api, List<QuerySpec> specs)
    {
        var signature = BuildSignature(specs);
        if (signature == _frameSignature && (specs.Count == 0 || _frameQuery != 0))
            return true;

        FreeFrameQuery();
        _frameSignature = null;

        if (specs.Count == 0)
        {
            _frameSignature = signature;
            return true;
        }

        var elements = new PresentMonNative.PM_QUERY_ELEMENT[specs.Count];
        for (var i = 0; i < specs.Count; i++)
            elements[i] = specs[i].Element;

        var status = api.RegisterFrameQuery(_session, out var query, elements, (ulong)elements.Length, out var blobSize);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            _lastError = $"PresentMon frame query failed: {PresentMonNative.StatusText(status)}";
            return false;
        }

        _frameQuery = query;
        _frameBlobSize = blobSize;

        lock (_sync)
        {
            _frameBindings.Clear();
            for (var i = 0; i < specs.Count; i++)
            {
                var binding = specs[i].Binding;
                binding.Element = elements[i];
                _frameBindings.Add(binding);
            }
        }

        var bytes = checked((long)Math.Max(1u, _frameBlobSize) * FrameBufferCapacity);
        _frameBlob = Marshal.AllocHGlobal(new IntPtr(bytes));
        _frameSignature = signature;
        return true;
    }

    private static string BuildSignature(List<QuerySpec> specs)
    {
        var builder = new StringBuilder(specs.Count * 12);
        foreach (var spec in specs)
        {
            var element = spec.Element;
            builder.Append((int)element.metric).Append('.')
                .Append((int)element.stat).Append('.')
                .Append(element.deviceId).Append('.')
                .Append(element.arrayIndex).Append(';');
        }

        return builder.ToString();
    }

    private List<QuerySpec> CollectDynamicSpecs(StatSet stats)
    {
        var specs = new List<QuerySpec>();

        if (stats.Contains(StatId.CpuUsage))
            AddScalar(specs, PresentMonNative.PM_METRIC.CPU_UTILIZATION, _systemDeviceId, MetricKey.CpuUsage);
        if (stats.Contains(StatId.PresentedFps))
            AddProcessScalar(specs, PresentMonNative.PM_METRIC.PRESENTED_FPS, MetricKey.PresentedFps);
        if (stats.Contains(StatId.DisplayedFps))
        {
            AddProcessScalar(specs, PresentMonNative.PM_METRIC.DISPLAYED_FPS, MetricKey.DisplayedFps);
            AddProcessScalar(specs, PresentMonNative.PM_METRIC.DISPLAYED_FRAME_TIME, MetricKey.DisplayedFrameTime);
        }
        if (stats.Contains(StatId.ApplicationFps))
            AddProcessScalar(specs, PresentMonNative.PM_METRIC.APPLICATION_FPS, MetricKey.ApplicationFps);

        if (stats.Contains(StatId.GpuTemperature)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_TEMPERATURE, MetricKey.GpuTemp);
        if (stats.Contains(StatId.GpuCoreClock)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_FREQUENCY, MetricKey.GpuCoreClock);
        if (stats.Contains(StatId.GpuMemoryClock)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_MEM_FREQUENCY, MetricKey.GpuMemClock);
        if (stats.Contains(StatId.GpuVramUsage)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_MEM_USED, MetricKey.GpuVram);
        if (stats.Contains(StatId.GpuVramPercent)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_MEM_UTILIZATION, MetricKey.GpuVramUtil);
        if (stats.Contains(StatId.GpuPower)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_POWER, MetricKey.GpuPower);
        if (stats.Contains(StatId.GpuUsage)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_UTILIZATION, MetricKey.GpuUsage);
        if (stats.Contains(StatId.GpuRenderCompute)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_RENDER_COMPUTE_UTILIZATION, MetricKey.GpuRenderCompute);
        if (stats.Contains(StatId.GpuPowerLimited)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_POWER_LIMITED, MetricKey.GpuPowerLimited);
        if (stats.Contains(StatId.GpuTemperatureLimited)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_TEMPERATURE_LIMITED, MetricKey.GpuTempLimited);
        if (stats.Contains(StatId.GpuCurrentLimited)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_CURRENT_LIMITED, MetricKey.GpuCurrentLimited);
        if (stats.Contains(StatId.GpuVoltageLimited)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_VOLTAGE_LIMITED, MetricKey.GpuVoltageLimited);
        if (stats.Contains(StatId.GpuUtilizationLimited)) AddGpuScalar(specs, PresentMonNative.PM_METRIC.GPU_UTILIZATION_LIMITED, MetricKey.GpuUtilLimited);

        return specs;
    }

    private List<QuerySpec> CollectFrameSpecs(StatSet stats)
    {
        var specs = new List<QuerySpec>();

        if (stats.Contains(StatId.CpuFrameTime)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.CPU_FRAME_TIME, MetricKey.CpuFrameTime);
        if (stats.Contains(StatId.CpuBusy)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.CPU_BUSY, MetricKey.CpuBusy);
        if (stats.Contains(StatId.CpuWait)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.CPU_WAIT, MetricKey.CpuWait);
        if (stats.Contains(StatId.GpuTime)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.GPU_TIME, MetricKey.GpuTime);
        if (stats.Contains(StatId.GpuBusy)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.GPU_BUSY, MetricKey.GpuBusy);
        if (stats.Contains(StatId.GpuWait)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.GPU_WAIT, MetricKey.GpuWait);
        if (stats.Contains(StatId.DroppedFrames)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.DROPPED_FRAMES, MetricKey.DroppedFrames);
        if (stats.Contains(StatId.GpuLatency)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.GPU_LATENCY, MetricKey.GpuLatency);
        if (stats.Contains(StatId.DisplayLatency)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.DISPLAY_LATENCY, MetricKey.DisplayLatency);
        if (stats.Contains(StatId.RenderPresentLatency)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.RENDER_PRESENT_LATENCY, MetricKey.RenderPresentLatency);
        if (stats.Contains(StatId.UntilDisplayed)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.UNTIL_DISPLAYED, MetricKey.UntilDisplayed);
        if (stats.Contains(StatId.BetweenDisplayChanges)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.BETWEEN_DISPLAY_CHANGE, MetricKey.BetweenDisplayChange);
        if (stats.Contains(StatId.ClickToPhotonLatency)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.CLICK_TO_PHOTON_LATENCY, MetricKey.ClickToPhotonLatency);
        if (stats.Contains(StatId.AllInputToPhotonLatency)) AddFrameMetric(specs, PresentMonNative.PM_METRIC.ALL_INPUT_TO_PHOTON_LATENCY, MetricKey.AllInputToPhotonLatency);

        // The between-presents metric feeds the frame history, so it is registered whenever either
        // the statistic itself or anything computed from the history is on (once, never twice).
        if (stats.Contains(StatId.BetweenPresents) || stats.Intersects(StatGroups.FrameHistory))
            AddFrameMetric(specs, PresentMonNative.PM_METRIC.BETWEEN_PRESENTS, MetricKey.BetweenPresents);

        return specs;
    }

    /// <summary>Frees both queries and their buffers. Values already gathered are kept and expire on their own.</summary>
    private void FreeQueries()
    {
        FreeDynamicQuery();
        FreeFrameQuery();
        _dynamicSignature = null;
        _frameSignature = null;
    }

    private void FreeDynamicQuery()
    {
        var api = _api;
        if (api is not null && _dynamicQuery != 0)
        {
            try { api.FreeDynamicQuery(_dynamicQuery); } catch { }
        }

        _dynamicQuery = 0;
        _dynamicBlobSize = 0;
        _dynamicBlobCapacity = 0;
        lock (_sync)
            _bindings.Clear();

        FreeBuffer(ref _dynamicBlob);
    }

    private void FreeFrameQuery()
    {
        var api = _api;
        if (api is not null && _frameQuery != 0)
        {
            try { api.FreeFrameQuery(_frameQuery); } catch { }
        }

        _frameQuery = 0;
        _frameBlobSize = 0;
        lock (_sync)
            _frameBindings.Clear();

        FreeBuffer(ref _frameBlob);
    }

    // ---- polling -----------------------------------------------------------------------------

    private void PollDynamicMetrics()
    {
        var api = _api;
        if (api is null || _dynamicQuery == 0 || _dynamicBlob == 0)
            return;

        ZeroMemory(_dynamicBlob, _dynamicBlobCapacity);

        uint swapChains = MaxSwapChains;
        var status = api.PollDynamicQuery(_dynamicQuery, (uint)TrackedPid, _dynamicBlob, ref swapChains);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            if (status != PresentMonNative.PM_STATUS.OUT_OF_RANGE)
                _lastError = $"PresentMon telemetry query failed: {PresentMonNative.StatusText(status)}";

            NoteStatus(status);
            return;
        }

        NoteStatus(status);

        swapChains = Math.Min(swapChains, (uint)MaxSwapChains);
        if (swapChains == 0)
            return;

        var now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _gpuSamples.Clear();
            _gpuSamplesAt = now;
            _polledMask = 0;

            for (var swapChainIndex = 0; swapChainIndex < swapChains; swapChainIndex++)
            {
                foreach (var binding in _bindings)
                {
                    var value = TryReadValue(binding, swapChainIndex);
                    if (!value.HasValue || !double.IsFinite(value.Value))
                        continue;

                    var converted = ConvertToDisplayUnit(value.Value, binding.Unit);
                    switch (binding.Kind)
                    {
                        case BindingKind.ProcessScalar:
                        {
                            var index = (int)binding.Key;
                            var bit = 1UL << index;
                            var first = (_polledMask & bit) == 0;

                            // With several swap chains the highest FPS wins; other values keep the first.
                            if (first || (IsFpsKey(binding.Key) && converted > _values[index].Value))
                            {
                                _values[index] = new TimedValue(converted, now);
                                _polledMask |= bit;
                            }

                            break;
                        }
                        case BindingKind.GpuScalar:
                            if (!_gpuSamples.TryGetValue(binding.DeviceId, out var sample))
                            {
                                sample = new GpuSample(binding.DeviceId);
                                _gpuSamples[binding.DeviceId] = sample;
                            }

                            sample.Set(binding.Key, converted);
                            break;
                    }
                }
            }
        }
    }

    private static bool IsFpsKey(MetricKey key) =>
        key is MetricKey.PresentedFps or MetricKey.DisplayedFps or MetricKey.ApplicationFps;

    private void ConsumeFrames()
    {
        var api = _api;
        var pid = TrackedPid;
        if (api is null || _frameQuery == 0 || _frameBlob == 0 || pid == 0 || _frameBlobSize == 0)
            return;

        while (true)
        {
            uint requested = FrameBufferCapacity;
            var status = api.ConsumeFrames(_frameQuery, (uint)pid, _frameBlob, ref requested);
            if (status != PresentMonNative.PM_STATUS.SUCCESS)
            {
                NoteStatus(status);
                break;
            }

            NoteStatus(status);
            if (requested == 0)
                break;

            var now = Stopwatch.GetTimestamp();
            lock (_sync)
            {
                var stride = (int)_frameBlobSize;
                var batchCount = 0;

                for (var i = 0; i < requested; i++)
                {
                    foreach (var binding in _frameBindings)
                    {
                        var value = TryReadValue(binding, i, stride);
                        if (!value.HasValue || !double.IsFinite(value.Value))
                            continue;

                        var converted = ConvertToDisplayUnit(value.Value, binding.Unit);

                        if (IsInputLatencyKey(binding.Key))
                        {
                            // A zero means "no input event for this frame" and must not overwrite a real reading.
                            if (converted > 0)
                                _values[(int)binding.Key] = new TimedValue(converted, now);
                        }
                        else
                        {
                            _values[(int)binding.Key] = new TimedValue(converted, now);
                        }

                        if (binding.Key == MetricKey.BetweenPresents &&
                            converted > 0 && converted < MaxPlausibleFrameMs &&
                            batchCount < _frameScratch.Length)
                        {
                            _frameScratch[batchCount++] = converted;
                        }
                    }
                }

                _frames.AddBatch(_frameScratch.AsSpan(0, batchCount), now);
            }

            if (requested < FrameBufferCapacity)
                break;
        }
    }

    private static bool IsInputLatencyKey(MetricKey key) =>
        key is MetricKey.ClickToPhotonLatency or MetricKey.AllInputToPhotonLatency;

    private double? TryReadValue(Binding binding, int swapChainIndex)
    {
        var baseOffset = checked((long)_dynamicBlobSize * swapChainIndex);
        return TryReadValue(binding, Add(_dynamicBlob, baseOffset), binding.Element.dataOffset);
    }

    private double? TryReadValue(Binding binding, int frameIndex, int stride)
    {
        var baseOffset = checked((long)frameIndex * stride);
        return TryReadValue(binding, Add(_frameBlob, baseOffset), binding.Element.dataOffset);
    }

    private static double? TryReadValue(Binding binding, IntPtr blob, ulong dataOffset)
    {
        var address = Add(blob, checked((long)dataOffset));
        return binding.DataType switch
        {
            PresentMonNative.PM_DATA_TYPE.DOUBLE => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(address)),
            PresentMonNative.PM_DATA_TYPE.INT32 => (double)Marshal.ReadInt32(address),
            PresentMonNative.PM_DATA_TYPE.UINT32 => (double)unchecked((uint)Marshal.ReadInt32(address)),
            PresentMonNative.PM_DATA_TYPE.UINT64 => unchecked((double)Marshal.ReadInt64(address)),
            PresentMonNative.PM_DATA_TYPE.BOOL => Marshal.ReadByte(address) != 0 ? 1.0 : 0.0,
            PresentMonNative.PM_DATA_TYPE.ENUM => (double)Marshal.ReadInt32(address),
            _ => null,
        };
    }

    private static IntPtr Add(IntPtr pointer, long offset) =>
        new(checked(pointer.ToInt64() + offset));

    // ---- value access (callers hold _sync) ---------------------------------------------------

    private double? GetValue(MetricKey key, long now)
    {
        var entry = _values[(int)key];
        if (entry.Timestamp == 0 || now - entry.Timestamp > ValueExpiryTicks || !double.IsFinite(entry.Value))
            return null;

        return entry.Value;
    }

    private double? GetRateMetric(MetricKey key, long now, bool fallbackFromFrameTimes = false, bool fallbackFromDisplayedTime = false)
    {
        var direct = GetValue(key, now);
        if (direct.HasValue)
            return direct;

        if (fallbackFromDisplayedTime)
        {
            var displayedFrameTime = GetValue(MetricKey.DisplayedFrameTime, now);
            if (displayedFrameTime is > 0 and < MaxPlausibleFrameMs)
                return 1000.0 / displayedFrameTime.Value;

            var betweenDisplayChanges = GetValue(MetricKey.BetweenDisplayChange, now);
            if (betweenDisplayChanges is > 0 and < MaxPlausibleFrameMs)
                return 1000.0 / betweenDisplayChanges.Value;
        }

        // The current FPS from the frame history, not an average over the whole history.
        return fallbackFromFrameTimes ? _frames.CalculateFps(now) : null;
    }

    private GpuSample? ChooseActiveGpu(long now)
    {
        if (_gpuSamples.Count == 0 || now - _gpuSamplesAt > ValueExpiryTicks)
            return null;

        var anyGraphics = false;
        foreach (var sample in _gpuSamples.Values)
        {
            if (IsGraphicsDevice(sample))
            {
                anyGraphics = true;
                break;
            }
        }

        GpuSample? best = null;
        foreach (var sample in _gpuSamples.Values)
        {
            if (anyGraphics && !IsGraphicsDevice(sample))
                continue;

            if (best is null)
            {
                best = sample;
                continue;
            }

            var usage = sample.UsagePercent ?? -1;
            var bestUsage = best.UsagePercent ?? -1;
            if (usage > bestUsage || (usage == bestUsage && sample.HasUsefulData && !best.HasUsefulData))
                best = sample;
        }

        return best;
    }

    private bool IsGraphicsDevice(GpuSample sample) =>
        _deviceTypes.TryGetValue(sample.DeviceId, out var type) && type == PresentMonNative.PM_DEVICE_TYPE.GRAPHICS_ADAPTER;

    // ---- introspection -----------------------------------------------------------------------

    // Callers hold _sync.
    private void ReadIntrospection(IntPtr rootPtr)
    {
        var root = Marshal.PtrToStructure<PresentMonNative.PM_INTROSPECTION_ROOT>(rootPtr);

        _metrics = new Dictionary<PresentMonNative.PM_METRIC, MetricInfo>();
        _graphicsDeviceIds.Clear();
        _deviceTypes.Clear();
        _systemDeviceId = 0;
        _hasSystemDevice = false;
        _independentDeviceId = 0;
        _hasIndependentDevice = false;

        foreach (var device in EnumerateObjects<PresentMonNative.PM_INTROSPECTION_DEVICE>(root.pDevices))
        {
            _deviceTypes[device.id] = device.type;

            if (device.type == PresentMonNative.PM_DEVICE_TYPE.SYSTEM)
            {
                _systemDeviceId = device.id;
                _hasSystemDevice = true;
            }
            else if (device.type == PresentMonNative.PM_DEVICE_TYPE.GRAPHICS_ADAPTER)
                _graphicsDeviceIds.Add(device.id);
            else if (device.type == PresentMonNative.PM_DEVICE_TYPE.INDEPENDENT && !_hasIndependentDevice)
            {
                _independentDeviceId = device.id;
                _hasIndependentDevice = true;
            }
        }

        foreach (var metric in EnumerateObjects<PresentMonNative.PM_INTROSPECTION_METRIC>(root.pMetrics))
        {
            if (metric.pTypeInfo == 0)
                continue;

            var typeInfo = Marshal.PtrToStructure<PresentMonNative.PM_INTROSPECTION_DATA_TYPE_INFO>(metric.pTypeInfo);
            var stats = new HashSet<PresentMonNative.PM_STAT>();
            foreach (var statInfo in EnumerateObjects<PresentMonNative.PM_INTROSPECTION_STAT_INFO>(metric.pStatInfo))
                stats.Add(statInfo.stat);

            var info = new MetricInfo(
                metric.unit,
                metric.preferredUnitHint,
                typeInfo.polledType,
                typeInfo.frameType,
                metric.type,
                stats,
                new Dictionary<uint, uint>(),
                new Dictionary<uint, PresentMonNative.PM_METRIC_AVAILABILITY>());

            foreach (var deviceInfo in EnumerateObjects<PresentMonNative.PM_INTROSPECTION_DEVICE_METRIC_INFO>(metric.pDeviceMetricInfo))
            {
                info.DeviceAvailability[deviceInfo.deviceId] = deviceInfo.availability;
                if (deviceInfo.availability == PresentMonNative.PM_METRIC_AVAILABILITY.AVAILABLE)
                    info.DeviceArraySizes[deviceInfo.deviceId] = Math.Max(1u, deviceInfo.arraySize);
            }

            _metrics[metric.id] = info;
        }
    }

    private static IEnumerable<T> EnumerateObjects<T>(IntPtr arrayPtr) where T : struct
    {
        if (arrayPtr == 0)
            yield break;

        var array = Marshal.PtrToStructure<PresentMonNative.PM_INTROSPECTION_OBJARRAY>(arrayPtr);
        if (array.pData == 0 || array.size == 0)
            yield break;

        var count = array.size > (nuint)4096 ? (nuint)4096 : array.size;
        for (nuint i = 0; i < count; i++)
        {
            var objectPtr = Marshal.ReadIntPtr(array.pData, checked((int)(i * (nuint)IntPtr.Size)));
            if (objectPtr != 0)
                yield return Marshal.PtrToStructure<T>(objectPtr);
        }
    }

    // ---- query element construction (introspection is loaded when these run) -----------------

    private void AddProcessScalar(List<QuerySpec> specs, PresentMonNative.PM_METRIC metric, MetricKey key)
    {
        if (!_metrics.TryGetValue(metric, out var info))
            return;

        if (info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC && info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC_FRAME)
            return;

        var processDeviceId = ResolveProcessDeviceId(info);
        if (!processDeviceId.HasValue)
            return;

        var stat = info.Stats.Contains(PresentMonNative.PM_STAT.AVG)
            ? PresentMonNative.PM_STAT.AVG
            : PresentMonNative.PM_STAT.NEWEST_POINT;

        var element = CreateElement(metric, processDeviceId.Value, 0u, stat);
        specs.Add(new QuerySpec(element, new Binding(BindingKind.ProcessScalar, key, processDeviceId.Value, 0, info.PolledType, info.Unit, element)));
    }

    private void AddFrameMetric(List<QuerySpec> specs, PresentMonNative.PM_METRIC metric, MetricKey key)
    {
        if (!_metrics.TryGetValue(metric, out var info))
            return;

        if (info.MetricType != PresentMonNative.PM_METRIC_TYPE.FRAME_EVENT && info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC_FRAME)
            return;

        var processDeviceId = ResolveProcessDeviceId(info);
        if (!processDeviceId.HasValue)
            return;

        var element = new PresentMonNative.PM_QUERY_ELEMENT
        {
            metric = metric,
            stat = PresentMonNative.PM_STAT.NONE,
            deviceId = processDeviceId.Value,
            arrayIndex = 0,
        };

        specs.Add(new QuerySpec(element, new Binding(BindingKind.Frame, key, processDeviceId.Value, 0, info.FrameType, info.Unit, element)));
    }

    private uint? ResolveProcessDeviceId(MetricInfo info)
    {
        if (info.DeviceArraySizes.ContainsKey(0u))
            return 0u;

        if (_hasIndependentDevice && info.DeviceArraySizes.ContainsKey(_independentDeviceId))
            return _independentDeviceId;

        return null;
    }

    private void AddScalar(List<QuerySpec> specs, PresentMonNative.PM_METRIC metric, uint deviceId, MetricKey key)
    {
        if (!_hasSystemDevice || !_metrics.TryGetValue(metric, out var info) || !info.DeviceArraySizes.ContainsKey(deviceId))
            return;

        if (info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC && info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC_FRAME)
            return;

        var element = CreateElement(metric, deviceId, 0u);
        specs.Add(new QuerySpec(element, new Binding(BindingKind.ProcessScalar, key, deviceId, 0, info.PolledType, info.Unit, element)));
    }

    private void AddGpuScalar(List<QuerySpec> specs, PresentMonNative.PM_METRIC metric, MetricKey key)
    {
        if (!_metrics.TryGetValue(metric, out var info))
            return;

        if (info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC && info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC_FRAME)
            return;

        foreach (var deviceId in ResolveGpuMetricDeviceIds(info))
        {
            var element = CreateElement(metric, deviceId, 0u);
            specs.Add(new QuerySpec(element, new Binding(BindingKind.GpuScalar, key, deviceId, 0, info.PolledType, info.Unit, element)));
        }
    }

    private IEnumerable<uint> ResolveGpuMetricDeviceIds(MetricInfo info)
    {
        var yielded = new HashSet<uint>();

        foreach (var deviceId in _graphicsDeviceIds)
        {
            if (info.DeviceArraySizes.ContainsKey(deviceId) && yielded.Add(deviceId))
                yield return deviceId;
        }

        if (_hasIndependentDevice && info.DeviceArraySizes.ContainsKey(_independentDeviceId) && yielded.Add(_independentDeviceId))
            yield return _independentDeviceId;

        foreach (var deviceId in info.DeviceArraySizes.Keys)
        {
            if (_deviceTypes.TryGetValue(deviceId, out var type) && type == PresentMonNative.PM_DEVICE_TYPE.SYSTEM)
                continue;

            if (yielded.Add(deviceId))
                yield return deviceId;
        }
    }

    private PresentMonNative.PM_QUERY_ELEMENT CreateElement(
        PresentMonNative.PM_METRIC metric,
        uint deviceId,
        uint index,
        PresentMonNative.PM_STAT? requestedStat = null)
    {
        var stat = requestedStat ?? PresentMonNative.PM_STAT.NONE;
        if (requestedStat is null && _metrics.TryGetValue(metric, out var info))
        {
            if (info.Stats.Contains(PresentMonNative.PM_STAT.NEWEST_POINT))
                stat = PresentMonNative.PM_STAT.NEWEST_POINT;
            else if (info.Stats.Contains(PresentMonNative.PM_STAT.AVG))
                stat = PresentMonNative.PM_STAT.AVG;
        }

        return new PresentMonNative.PM_QUERY_ELEMENT
        {
            metric = metric,
            stat = stat,
            deviceId = deviceId,
            arrayIndex = index,
        };
    }

    private static double ConvertToDisplayUnit(double value, PresentMonNative.PM_UNIT unit) => unit switch
    {
        PresentMonNative.PM_UNIT.MICROSECONDS => value / 1000.0,
        PresentMonNative.PM_UNIT.SECONDS => value * 1000.0,
        PresentMonNative.PM_UNIT.MINUTES => value * 60000.0,
        PresentMonNative.PM_UNIT.HOURS => value * 3600000.0,
        PresentMonNative.PM_UNIT.RATIO => value * 100.0,
        PresentMonNative.PM_UNIT.MILLIWATTS => value / 1000.0,
        PresentMonNative.PM_UNIT.KILOWATTS => value * 1000.0,
        PresentMonNative.PM_UNIT.MILLIVOLTS => value / 1000.0,
        PresentMonNative.PM_UNIT.KILOHERTZ => value * 1000.0,
        PresentMonNative.PM_UNIT.HERTZ => value / 1_000_000.0,
        PresentMonNative.PM_UNIT.GIGAHERTZ => value * 1000.0,
        PresentMonNative.PM_UNIT.BYTES => value / 1048576.0,
        PresentMonNative.PM_UNIT.KILOBYTES => value / 1024.0,
        PresentMonNative.PM_UNIT.GIGABYTES => value * 1024.0,
        _ => value,
    };

    // ---- buffers and lifetime ----------------------------------------------------------------

    private static void FreeBuffer(ref IntPtr buffer)
    {
        if (buffer == 0)
            return;

        Marshal.FreeHGlobal(buffer);
        buffer = 0;
    }

    private static void ZeroMemory(IntPtr pointer, uint bytes)
    {
        unsafe
        {
            new Span<byte>((void*)pointer, checked((int)Math.Min(bytes, int.MaxValue))).Clear();
        }
    }

    public void Dispose()
    {
        StopTracking();

        var api = _api;
        if (api is not null && _session != 0)
        {
            try { api.CloseSession(_session); } catch { }
            _session = 0;
        }

        api?.Dispose();
        _api = null;
        GC.SuppressFinalize(this);
    }

    // ---- types -------------------------------------------------------------------------------

    private enum MetricKey
    {
        // Polled process / system scalars
        CpuUsage, PresentedFps, DisplayedFps, DisplayedFrameTime, ApplicationFps,

        // Polled GPU scalars
        GpuTemp, GpuCoreClock, GpuMemClock, GpuVram, GpuVramUtil, GpuPower, GpuUsage, GpuRenderCompute,
        GpuPowerLimited, GpuTempLimited, GpuCurrentLimited, GpuVoltageLimited, GpuUtilLimited,

        // Per-frame values
        CpuFrameTime, CpuBusy, CpuWait, GpuTime, GpuBusy, GpuWait, DroppedFrames, GpuLatency,
        DisplayLatency, RenderPresentLatency, UntilDisplayed, BetweenPresents, BetweenDisplayChange,
        ClickToPhotonLatency, AllInputToPhotonLatency,

        Count,
    }

    private readonly record struct TimedValue(double Value, long Timestamp);

    private readonly record struct MetricInfo(
        PresentMonNative.PM_UNIT Unit,
        PresentMonNative.PM_UNIT PreferredUnit,
        PresentMonNative.PM_DATA_TYPE PolledType,
        PresentMonNative.PM_DATA_TYPE FrameType,
        PresentMonNative.PM_METRIC_TYPE MetricType,
        HashSet<PresentMonNative.PM_STAT> Stats,
        Dictionary<uint, uint> DeviceArraySizes,
        Dictionary<uint, PresentMonNative.PM_METRIC_AVAILABILITY> DeviceAvailability);

    private enum BindingKind
    {
        ProcessScalar,
        GpuScalar,
        Frame,
    }

    private sealed class Binding
    {
        internal BindingKind Kind { get; }
        internal MetricKey Key { get; }
        internal uint DeviceId { get; }
        internal int ArrayIndex { get; }
        internal PresentMonNative.PM_DATA_TYPE DataType { get; }
        internal PresentMonNative.PM_UNIT Unit { get; }
        internal PresentMonNative.PM_QUERY_ELEMENT Element { get; set; }

        internal Binding(BindingKind kind, MetricKey key, uint deviceId, int arrayIndex, PresentMonNative.PM_DATA_TYPE dataType, PresentMonNative.PM_UNIT unit, PresentMonNative.PM_QUERY_ELEMENT element)
        {
            Kind = kind;
            Key = key;
            DeviceId = deviceId;
            ArrayIndex = arrayIndex;
            DataType = dataType;
            Unit = unit;
            Element = element;
        }
    }

    private readonly record struct QuerySpec(PresentMonNative.PM_QUERY_ELEMENT Element, Binding Binding);

    private sealed class GpuSample
    {
        internal uint DeviceId { get; }
        internal double? TemperatureC { get; private set; }
        internal double? CoreClockMHz { get; private set; }
        internal double? MemoryClockMHz { get; private set; }
        internal double? VramUsedMb { get; private set; }
        internal double? VramUsagePercent { get; private set; }
        internal double? PowerW { get; private set; }
        internal double? UsagePercent { get; private set; }
        internal double? RenderComputeUsagePercent { get; private set; }
        internal double? PowerLimited { get; private set; }
        internal double? TemperatureLimited { get; private set; }
        internal double? CurrentLimited { get; private set; }
        internal double? VoltageLimited { get; private set; }
        internal double? UtilizationLimited { get; private set; }

        internal bool HasUsefulData =>
            TemperatureC.HasValue || CoreClockMHz.HasValue || PowerW.HasValue ||
            UsagePercent.HasValue || VramUsedMb.HasValue || RenderComputeUsagePercent.HasValue;

        internal GpuSample(uint deviceId) => DeviceId = deviceId;

        internal void Set(MetricKey key, double value)
        {
            switch (key)
            {
                case MetricKey.GpuTemp: TemperatureC = value; break;
                case MetricKey.GpuCoreClock: CoreClockMHz = value; break;
                case MetricKey.GpuMemClock: MemoryClockMHz = value; break;
                case MetricKey.GpuVram: VramUsedMb = value; break;
                case MetricKey.GpuVramUtil:
                    VramUsagePercent = double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : null;
                    break;
                case MetricKey.GpuPower: PowerW = value; break;
                case MetricKey.GpuUsage: UsagePercent = value; break;
                case MetricKey.GpuRenderCompute: RenderComputeUsagePercent = value; break;
                case MetricKey.GpuPowerLimited: PowerLimited = value; break;
                case MetricKey.GpuTempLimited: TemperatureLimited = value; break;
                case MetricKey.GpuCurrentLimited: CurrentLimited = value; break;
                case MetricKey.GpuVoltageLimited: VoltageLimited = value; break;
                case MetricKey.GpuUtilLimited: UtilizationLimited = value; break;
            }
        }
    }
}
