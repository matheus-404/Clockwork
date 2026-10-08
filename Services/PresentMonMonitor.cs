using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clockwork.Services;

/// <summary>
/// PresentMon Service client used by Clockwork for FPS, CPU and GPU telemetry.
/// RAM usage remains in Win32 because it is a Windows system-memory statistic rather than a
/// PresentMon hardware metric.
/// </summary>
public sealed class PresentMonMonitor : IDisposable
{
    private const int MaxFrames = 20_000;
    private const int MaxSwapChains = 64;
    private const uint TelemetryPollingPeriodMs = 100;
    private const uint EtwFlushPeriodMs = 16;
    private const double DynamicWindowMs = 500.0;

    private readonly double[] _frameTimesMs = new double[MaxFrames];
    private readonly double[] _lowFpsScratch = new double[MaxFrames];
    private int _frameHead;
    private int _frameCount;
    private double _frameSumMs;
    private readonly object _sync = new();
    private readonly Stopwatch _averageFpsStopwatch = new();
    private long _averageFpsFrameCount;
    private bool _averageFpsMeasurementStarted;

    private PresentMonNative.Api? _api;
    private IntPtr _session;
    private IntPtr _dynamicQuery;
    private IntPtr _frameQuery;
    private IntPtr _dynamicBlob;
    private IntPtr _frameBlob;
    private int _trackedPid;
    private uint _dynamicBlobSize;
    private uint _frameBlobSize;
    private uint _dynamicBlobCapacity;
    private uint _frameBufferCapacity;
    private string? _lastError;
    private PresentMonMetricPlan _metricPlan;
    private Dictionary<PresentMonNative.PM_METRIC, MetricInfo> _metrics = new();
    private readonly Dictionary<uint, string> _deviceNames = new();
    private readonly List<uint> _graphicsDeviceIds = new();
    private readonly Dictionary<uint, PresentMonNative.PM_DEVICE_TYPE> _deviceTypes = new();
    private uint _systemDeviceId;
    private bool _hasSystemDevice;
    private uint _independentDeviceId;
    private bool _hasIndependentDevice;
    private readonly List<Binding> _bindings = new();
    private readonly List<Binding> _frameBindings = new();
    private readonly Dictionary<uint, GpuSample> _gpuSamples = new();
    private readonly Dictionary<string, double> _processValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _polledScalarKeys = new(StringComparer.Ordinal);

    private static readonly long StaleAfterEmptyTicks = Stopwatch.Frequency * 2;
    private long _emptySinceTimestamp;
    private readonly Dictionary<string, double> _frameValues = new(StringComparer.Ordinal);

    public bool IsAvailable => _session != 0 && _api is not null;
    public bool IsTracking => _trackedPid != 0;

    public void StartTracking(int pid, PresentMonMetricPlan plan)
    {
        if (pid <= 0 || !plan.HasAny)
        {
            StopTracking();
            return;
        }

        if (_trackedPid == pid && IsAvailable && plan.Equals(_metricPlan))
            return;

        if (_trackedPid == pid && IsAvailable)
        {
            _metricPlan = plan;
            try
            {
                if (!BuildQueries())
                {
                    _lastError ??= "PresentMon queries could not be created.";
                    StopTrackingQueriesOnly();
                }

                lock (_sync)
                {
                    if (plan.AvgFps)
                    {
                        ResetAverageFpsMeasurement();
                        _averageFpsStopwatch.Start();
                        _averageFpsMeasurementStarted = true;
                    }
                    else
                    {
                        ResetAverageFpsMeasurement();
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = $"PresentMon query rebuild error: {ex.Message}";
                StopTrackingQueriesOnly();
            }
            return;
        }

        StopTracking();
        _trackedPid = pid;
        _metricPlan = plan;

        try
        {
            if (!EnsureSession() || _api is null)
                return;

            var api = _api;
            var status = api.StartTrackingProcess(_session, (uint)pid);
            if (status != PresentMonNative.PM_STATUS.SUCCESS &&
                status != PresentMonNative.PM_STATUS.ALREADY_TRACKING_PROCESS)
            {
                _lastError = $"PresentMon could not track PID {pid}: {PresentMonNative.StatusText(status)}";
                _trackedPid = 0;
                return;
            }

            api.SetTelemetryPollingPeriod(_session, 0u, TelemetryPollingPeriodMs);
            api.SetEtwFlushPeriod(_session, EtwFlushPeriodMs);

            if (!BuildQueries())
            {
                _lastError ??= "PresentMon queries could not be created.";
                StopTrackingQueriesOnly();
            }
            else if (plan.AvgFps)
            {
                lock (_sync)
                {
                    ResetAverageFpsMeasurement();
                    _averageFpsStopwatch.Start();
                    _averageFpsMeasurementStarted = true;
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon initialization error: {ex.Message}";
            StopTrackingQueriesOnly();
            _trackedPid = 0;
        }
    }

    public void StopTracking()
    {
        var api = _api;
        if (api is not null && _session != 0 && _trackedPid != 0)
        {
            try { api.StopTrackingProcess(_session, (uint)_trackedPid); } catch { }
        }

        StopTrackingQueriesOnly();
        _trackedPid = 0;

        lock (_sync)
        {
            ResetFrameHistory();
            _gpuSamples.Clear();
            _processValues.Clear();
            _emptySinceTimestamp = 0;
            _frameValues.Clear();
            ResetAverageFpsMeasurement();
        }
    }

    public void Update()
    {
        if (_trackedPid == 0)
            return;

        if (!IsAvailable)
        {
            var pid = _trackedPid;
            StopTrackingQueriesOnly();
            _trackedPid = 0;
            StartTracking(pid, _metricPlan);
            return;
        }

        try
        {
            if (_dynamicQuery == 0 && _frameQuery == 0)
            {
                if (!BuildQueries())
                    return;
            }

            PollDynamicMetrics();
            ConsumeFrames();
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon telemetry error: {ex.Message}";
            StopTrackingQueriesOnly();
        }
    }

    public void UpdateFastFrameMetrics()
    {
        if (_trackedPid == 0 || !IsAvailable || _frameQuery == 0 || _frameBlob == 0)
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

    public PresentMonFastFrameSnapshot GetFastFrameSnapshot(PresentMonFastFramePlan plan)
    {
        lock (_sync)
        {
            return new PresentMonFastFrameSnapshot(
                plan.Fps ? CalculateFps() : null,
                plan.GpuLatency ? GetMetric(GpuLatencyKey) : null,
                plan.DisplayLatency ? GetMetric(DisplayLatencyKey) : null,
                plan.RenderPresentLatency ? GetMetric(RenderPresentLatencyKey) : null,
                plan.UntilDisplayed ? GetMetric(UntilDisplayedKey) : null,
                plan.BetweenPresents ? GetMetric(BetweenPresentsKey) : null,
                plan.BetweenDisplayChanges ? GetMetric(BetweenDisplayChangeKey) : null,
                plan.ClickToPhotonLatency ? GetMetric(ClickToPhotonLatencyKey) : null,
                plan.AllInputToPhotonLatency ? GetMetric(AllInputToPhotonLatencyKey) : null);
        }
    }

    public PresentMonSnapshot GetSnapshot(PresentMonMetricPlan plan)
    {
        lock (_sync)
        {
            var gpu = plan.HasGpuMetric ? ChooseActiveGpu() : null;
            (double? low1, double? low01) = (plan.Low1Fps || plan.Low01Fps)
                ? CalculateLowFpsPair()
                : (null, null);

            return new PresentMonSnapshot(
                Array.Empty<CpuCoreFrequency>(),
                plan.CpuUsage ? GetScalar(CpuUsageKey) : null,
                plan.GpuTemperature ? gpu?.TemperatureC : null,
                plan.GpuCoreClock ? gpu?.CoreClockMHz : null,
                plan.GpuMemoryClock ? gpu?.MemoryClockMHz : null,
                plan.GpuVramUsage ? gpu?.VramUsedMb : null,
                plan.VramUsagePercent ? gpu?.VramUsagePercent : null,
                plan.GpuPower ? gpu?.PowerW : null,
                plan.GpuUsage ? gpu?.UsagePercent : null,
                plan.GpuRenderCompute ? gpu?.RenderComputeUsagePercent : null,
                plan.GpuPowerLimited ? gpu?.PowerLimited : null,
                plan.GpuTemperatureLimited ? gpu?.TemperatureLimited : null,
                plan.GpuCurrentLimited ? gpu?.CurrentLimited : null,
                plan.GpuVoltageLimited ? gpu?.VoltageLimited : null,
                plan.GpuUtilizationLimited ? gpu?.UtilizationLimited : null,
                plan.Fps ? CalculateFps() : null,
                plan.AvgFps ? CalculateAverageFps() : null,
                plan.Low1Fps ? low1 : null,
                plan.Low01Fps ? low01 : null,
                plan.FrameTime ? CalculateFrameTime() : null,
                plan.CpuFrameTime ? GetMetric(CpuFrameTimeKey) : null,
                plan.CpuBusy ? GetMetric(CpuBusyKey) : null,
                plan.CpuWait ? GetMetric(CpuWaitKey) : null,
                plan.PresentedFps ? GetRateMetric(PresentedFpsKey, fallbackFromFrameTimes: true) : null,
                plan.DisplayedFps ? GetRateMetric(DisplayedFpsKey, fallbackFromDisplayedTime: true) : null,
                plan.ApplicationFps ? GetRateMetric(ApplicationFpsKey, fallbackFromFrameTimes: true) : null,
                plan.GpuTime ? GetMetric(GpuTimeKey) : null,
                plan.GpuBusy ? GetMetric(GpuBusyKey) : null,
                plan.GpuWait ? GetMetric(GpuWaitKey) : null,
                plan.DroppedFrames ? GetMetric(DroppedFramesKey) : null,
                plan.GpuLatency ? GetMetric(GpuLatencyKey) : null,
                plan.DisplayLatency ? GetMetric(DisplayLatencyKey) : null,
                plan.RenderPresentLatency ? GetMetric(RenderPresentLatencyKey) : null,
                plan.UntilDisplayed ? GetMetric(UntilDisplayedKey) : null,
                plan.BetweenPresents ? GetMetric(BetweenPresentsKey) : null,
                plan.BetweenDisplayChanges ? GetMetric(BetweenDisplayChangeKey) : null,
                plan.ClickToPhotonLatency ? GetMetric(ClickToPhotonLatencyKey) : null,
                plan.AllInputToPhotonLatency ? GetMetric(AllInputToPhotonLatencyKey) : null,
                IsAvailable,
                _lastError);
        }
    }

    private const string CpuUsageKey = "cpu-usage";
    private const string CpuFrameTimeKey = "cpu-frame-time";
    private const string CpuBusyKey = "cpu-busy";
    private const string CpuWaitKey = "cpu-wait";
    private const string PresentedFpsKey = "presented-fps";
    private const string DisplayedFpsKey = "displayed-fps";
    private const string ApplicationFpsKey = "application-fps";
    private const string GpuTimeKey = "gpu-time";
    private const string GpuBusyKey = "gpu-busy";
    private const string GpuWaitKey = "gpu-wait";
    private const string DroppedFramesKey = "dropped-frames";
    private const string GpuVramUtilizationKey = "vram-utilization";
    private const string DisplayedFrameTimeKey = "displayed-frame-time";
    private const string GpuLatencyKey = "gpu-latency";
    private const string DisplayLatencyKey = "display-latency";
    private const string RenderPresentLatencyKey = "render-present-latency";
    private const string UntilDisplayedKey = "until-displayed";
    private const string BetweenPresentsKey = "between-presents";
    private const string BetweenDisplayChangeKey = "between-display-change";
    private const string ClickToPhotonLatencyKey = "click-to-photon-latency";
    private const string AllInputToPhotonLatencyKey = "all-input-to-photon-latency";

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

    private bool BuildQueries()
    {
        var api = _api;
        if (api is null || _session == 0)
            return false;

        StopTrackingQueriesOnly();

        var rootStatus = api.GetIntrospectionRoot(_session, out var rootPtr);
        if (rootStatus != PresentMonNative.PM_STATUS.SUCCESS || rootPtr == 0)
        {
            _lastError = $"PresentMon introspection failed: {PresentMonNative.StatusText(rootStatus)}";
            return false;
        }

        try
        {
            lock (_sync)
            {
                ReadIntrospection(rootPtr);
            }
            BuildDynamicQuery(_metricPlan);
            BuildFrameQuery(_metricPlan);
            return _dynamicQuery != 0 || _frameQuery != 0;
        }
        catch (Exception ex)
        {
            _lastError = $"PresentMon query setup error: {ex.Message}";
            StopTrackingQueriesOnly();
            return false;
        }
        finally
        {
            try { api.FreeIntrospectionRoot(rootPtr); } catch { }
        }
    }

    private void ReadIntrospection(IntPtr rootPtr)
    {
        var root = Marshal.PtrToStructure<PresentMonNative.PM_INTROSPECTION_ROOT>(rootPtr);
        _metrics = new Dictionary<PresentMonNative.PM_METRIC, MetricInfo>();
        _graphicsDeviceIds.Clear();
        _deviceTypes.Clear();
        _deviceNames.Clear();
        _systemDeviceId = 0;
        _hasSystemDevice = false;
        _independentDeviceId = 0;
        _hasIndependentDevice = false;

        foreach (var device in EnumerateObjects<PresentMonNative.PM_INTROSPECTION_DEVICE>(root.pDevices))
        {
            _deviceTypes[device.id] = device.type;
            _deviceNames[device.id] = ReadIntrospectionString(device.pName) ?? "<unnamed>";

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

    private static string? ReadIntrospectionString(IntPtr pString)
    {
        if (pString == 0)
            return null;

        try
        {
            var value = Marshal.PtrToStructure<PresentMonNative.PM_INTROSPECTION_STRING>(pString);
            return value.pData == 0 ? null : Marshal.PtrToStringAnsi(value.pData);
        }
        catch
        {
            return null;
        }
    }

    private bool IsMetricBound(PresentMonNative.PM_METRIC metric)
    {
        lock (_sync)
            return _bindings.Any(x => x.Element.metric == metric) || _frameBindings.Any(x => x.Element.metric == metric);
    }

    internal string? GetMetricStatusText(PresentMonNative.PM_METRIC metric)
    {
        lock (_sync)
        {
            if (_trackedPid == 0 || !IsAvailable)
                return null;

            if (IsMetricBound(metric))
                return null;

            if (!_metrics.TryGetValue(metric, out var info))
                return "Unsupported";

            var availability = info.DeviceAvailability.Values.ToList();
            if (availability.Any(static x => x == PresentMonNative.PM_METRIC_AVAILABILITY.AVAILABLE))
                return null;

            if (availability.Any(static x =>
                    x == PresentMonNative.PM_METRIC_AVAILABILITY.NOT_EXPORTED_BY_SOURCE ||
                    x == PresentMonNative.PM_METRIC_AVAILABILITY.NOT_SUPPORTED_BY_DEVICE ||
                    x == PresentMonNative.PM_METRIC_AVAILABILITY.NOT_IMPLEMENTED_BY_PRESENTMON))
                return "Unsupported";

            return availability.Any(static x => x == PresentMonNative.PM_METRIC_AVAILABILITY.UNAVAILABLE)
                ? "Unavailable"
                : "Unsupported";
        }
    }

    private void BuildDynamicQuery(PresentMonMetricPlan plan)
    {
        var api = _api;
        if (api is null || _session == 0)
            return;

        var elements = new List<QuerySpec>();

        if (plan.CpuUsage)
            AddScalar(elements, PresentMonNative.PM_METRIC.CPU_UTILIZATION, _systemDeviceId, CpuUsageKey);

        if (plan.PresentedFps)
            AddProcessScalar(elements, PresentMonNative.PM_METRIC.PRESENTED_FPS, PresentedFpsKey);
        if (plan.DisplayedFps)
        {
            AddProcessScalar(elements, PresentMonNative.PM_METRIC.DISPLAYED_FPS, DisplayedFpsKey);
            AddProcessScalar(elements, PresentMonNative.PM_METRIC.DISPLAYED_FRAME_TIME, DisplayedFrameTimeKey);
        }
        if (plan.ApplicationFps)
            AddProcessScalar(elements, PresentMonNative.PM_METRIC.APPLICATION_FPS, ApplicationFpsKey);

        if (plan.GpuTemperature) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_TEMPERATURE, "temp");
        if (plan.GpuCoreClock) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_FREQUENCY, "core-clock");
        if (plan.GpuMemoryClock) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_MEM_FREQUENCY, "mem-clock");
        if (plan.GpuVramUsage) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_MEM_USED, "vram");
        if (plan.VramUsagePercent) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_MEM_UTILIZATION, GpuVramUtilizationKey);
        if (plan.GpuPower) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_POWER, "power");
        if (plan.GpuUsage) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_UTILIZATION, "usage");

        if (plan.GpuRenderCompute) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_RENDER_COMPUTE_UTILIZATION, "render-compute");
        if (plan.GpuPowerLimited) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_POWER_LIMITED, "power-limited");
        if (plan.GpuTemperatureLimited) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_TEMPERATURE_LIMITED, "temperature-limited");
        if (plan.GpuCurrentLimited) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_CURRENT_LIMITED, "current-limited");
        if (plan.GpuVoltageLimited) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_VOLTAGE_LIMITED, "voltage-limited");
        if (plan.GpuUtilizationLimited) AddGpuScalar(elements, PresentMonNative.PM_METRIC.GPU_UTILIZATION_LIMITED, "utilization-limited");

        if (elements.Count == 0)
            return;

        var nativeElements = elements.Select(x => x.Element).ToArray();
        var status = api.RegisterDynamicQuery(_session, out _dynamicQuery, nativeElements, (ulong)nativeElements.Length, DynamicWindowMs, 0u);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            _dynamicQuery = 0;
            _lastError = $"PresentMon dynamic query failed: {PresentMonNative.StatusText(status)}";
            return;
        }

        lock (_sync)
        {
            _bindings.Clear();
            for (var i = 0; i < elements.Count; i++)
            {
                var binding = elements[i].Binding;
                binding.Element = nativeElements[i];
                _bindings.Add(binding);
            }
        }

        _dynamicBlobSize = (uint)Math.Min(uint.MaxValue, nativeElements.Max(e => e.dataOffset + e.dataSize));
        _dynamicBlobCapacity = Math.Max(4096u, checked(_dynamicBlobSize * (uint)MaxSwapChains));
        _dynamicBlob = Marshal.AllocHGlobal((nint)_dynamicBlobCapacity);
    }

    private void BuildFrameQuery(PresentMonMetricPlan plan)
    {
        var api = _api;
        if (api is null || _session == 0)
            return;

        var elements = new List<QuerySpec>();

        if (plan.CpuFrameTime) AddFrameMetric(elements, PresentMonNative.PM_METRIC.CPU_FRAME_TIME, CpuFrameTimeKey);
        if (plan.CpuBusy) AddFrameMetric(elements, PresentMonNative.PM_METRIC.CPU_BUSY, CpuBusyKey);
        if (plan.CpuWait) AddFrameMetric(elements, PresentMonNative.PM_METRIC.CPU_WAIT, CpuWaitKey);
        if (plan.GpuTime) AddFrameMetric(elements, PresentMonNative.PM_METRIC.GPU_TIME, GpuTimeKey);
        if (plan.GpuBusy) AddFrameMetric(elements, PresentMonNative.PM_METRIC.GPU_BUSY, GpuBusyKey);
        if (plan.GpuWait) AddFrameMetric(elements, PresentMonNative.PM_METRIC.GPU_WAIT, GpuWaitKey);
        if (plan.DroppedFrames) AddFrameMetric(elements, PresentMonNative.PM_METRIC.DROPPED_FRAMES, DroppedFramesKey);
        if (plan.GpuLatency) AddFrameMetric(elements, PresentMonNative.PM_METRIC.GPU_LATENCY, GpuLatencyKey);
        if (plan.DisplayLatency) AddFrameMetric(elements, PresentMonNative.PM_METRIC.DISPLAY_LATENCY, DisplayLatencyKey);
        if (plan.RenderPresentLatency) AddFrameMetric(elements, PresentMonNative.PM_METRIC.RENDER_PRESENT_LATENCY, RenderPresentLatencyKey);
        if (plan.UntilDisplayed) AddFrameMetric(elements, PresentMonNative.PM_METRIC.UNTIL_DISPLAYED, UntilDisplayedKey);
        if (plan.BetweenPresents && !plan.RequiresFrameHistory) AddFrameMetric(elements, PresentMonNative.PM_METRIC.BETWEEN_PRESENTS, BetweenPresentsKey);
        if (plan.BetweenDisplayChanges) AddFrameMetric(elements, PresentMonNative.PM_METRIC.BETWEEN_DISPLAY_CHANGE, BetweenDisplayChangeKey);
        if (plan.ClickToPhotonLatency) AddFrameMetric(elements, PresentMonNative.PM_METRIC.CLICK_TO_PHOTON_LATENCY, ClickToPhotonLatencyKey);
        if (plan.AllInputToPhotonLatency) AddFrameMetric(elements, PresentMonNative.PM_METRIC.ALL_INPUT_TO_PHOTON_LATENCY, AllInputToPhotonLatencyKey);
        if (plan.RequiresFrameHistory) AddFrameMetric(elements, PresentMonNative.PM_METRIC.BETWEEN_PRESENTS, BetweenPresentsKey);

        if (elements.Count == 0)
            return;

        var nativeElements = elements.Select(x => x.Element).ToArray();
        var status = api.RegisterFrameQuery(_session, out _frameQuery, nativeElements, (ulong)nativeElements.Length, out _frameBlobSize);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            _frameQuery = 0;
            _frameBlobSize = 0;
            _lastError = $"PresentMon frame query failed: {PresentMonNative.StatusText(status)}";
            return;
        }

        lock (_sync)
        {
            _frameBindings.Clear();
            for (var i = 0; i < elements.Count; i++)
            {
                var binding = elements[i].Binding;
                binding.Element = nativeElements[i];
                _frameBindings.Add(binding);
            }
        }

        _frameBufferCapacity = Math.Max(64u, Math.Min(8192u, 4096u));
        var bytes = checked((long)Math.Max(1u, _frameBlobSize) * _frameBufferCapacity);
        _frameBlob = Marshal.AllocHGlobal(new IntPtr(bytes));
    }

    private void PollDynamicMetrics()
    {
        var api = _api;
        if (api is null || _dynamicQuery == 0 || _dynamicBlob == 0)
            return;

        ZeroMemory(_dynamicBlob, _dynamicBlobCapacity);
        uint swapChains = MaxSwapChains;
        var status = api.PollDynamicQuery(_dynamicQuery, (uint)_trackedPid, _dynamicBlob, ref swapChains);
        if (status != PresentMonNative.PM_STATUS.SUCCESS)
        {
            if (status != PresentMonNative.PM_STATUS.OUT_OF_RANGE)
                _lastError = $"PresentMon telemetry query failed: {PresentMonNative.StatusText(status)}";
            MarkEmptyPoll();
            return;
        }

        swapChains = Math.Min(swapChains, (uint)MaxSwapChains);
        if (swapChains == 0)
        {
            MarkEmptyPoll();
            return;
        }

        lock (_sync)
        {
            _emptySinceTimestamp = 0;
            _gpuSamples.Clear();
            _polledScalarKeys.Clear();

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
                            if (_polledScalarKeys.Add(binding.Key) ||
                                (IsFpsKey(binding.Key) &&
                                 (!_processValues.TryGetValue(binding.Key, out var existing) ||
                                  !double.IsFinite(existing) ||
                                  converted > existing)))
                            {
                                _processValues[binding.Key] = converted;
                            }
                            break;

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

    private void MarkEmptyPoll()
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            if (_emptySinceTimestamp == 0)
                _emptySinceTimestamp = now;

            if (now - _emptySinceTimestamp >= StaleAfterEmptyTicks)
            {
                _processValues.Clear();
                _gpuSamples.Clear();
            }
        }
    }

    private static bool IsFpsKey(string key) =>
        key is PresentedFpsKey or DisplayedFpsKey or ApplicationFpsKey;

    private void ConsumeFrames()
    {
        var api = _api;
        if (api is null || _frameQuery == 0 || _frameBlob == 0 || _trackedPid == 0 || _frameBlobSize == 0)
            return;

        while (true)
        {
            uint requested = _frameBufferCapacity;
            var status = api.ConsumeFrames(_frameQuery, (uint)_trackedPid, _frameBlob, ref requested);
            if (status != PresentMonNative.PM_STATUS.SUCCESS || requested == 0)
                break;

            lock (_sync)
            {
                var stride = (int)_frameBlobSize;
                for (var i = 0; i < requested; i++)
                {
                    foreach (var binding in _frameBindings)
                    {
                        var value = TryReadValue(binding, i, stride);
                        if (!value.HasValue || !double.IsFinite(value.Value))
                            continue;

                        var converted = ConvertToDisplayUnit(value.Value, binding.Unit);
                        _frameValues[binding.Key] = converted;

                        if (binding.Key == BetweenPresentsKey && converted > 0 && converted < 10000)
                        {
                            AddFrameTime(converted);
                            if (_averageFpsMeasurementStarted)
                                _averageFpsFrameCount++;
                        }
                    }
                }
            }

            if (requested < _frameBufferCapacity)
                break;
        }
    }

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
            PresentMonNative.PM_DATA_TYPE.DOUBLE => Marshal.PtrToStructure<double>(address),
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

    private void AddProcessScalar(List<QuerySpec> elements, PresentMonNative.PM_METRIC metric, string key)
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
        elements.Add(new QuerySpec(element, new Binding(BindingKind.ProcessScalar, key, processDeviceId.Value, 0, info.PolledType, info.Unit, element)));
    }

    private void AddFrameMetric(List<QuerySpec> elements, PresentMonNative.PM_METRIC metric, string key)
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

        elements.Add(new QuerySpec(element, new Binding(BindingKind.Frame, key, processDeviceId.Value, 0, info.FrameType, info.Unit, element)));
    }

    private uint? ResolveProcessDeviceId(MetricInfo info)
    {
        if (info.DeviceArraySizes.ContainsKey(0u))
            return 0u;
        if (_hasIndependentDevice && info.DeviceArraySizes.ContainsKey(_independentDeviceId))
            return _independentDeviceId;
        return null;
    }

    private void AddScalar(List<QuerySpec> elements, PresentMonNative.PM_METRIC metric, uint deviceId, string key)
    {
        if (!_hasSystemDevice || !_metrics.TryGetValue(metric, out var info) || !info.DeviceArraySizes.ContainsKey(deviceId))
            return;
        if (info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC && info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC_FRAME)
            return;

        var element = CreateElement(metric, deviceId, 0u);
        elements.Add(new QuerySpec(element, new Binding(BindingKind.ProcessScalar, key, deviceId, 0, info.PolledType, info.Unit, element)));
    }

    private void AddGpuScalar(List<QuerySpec> elements, PresentMonNative.PM_METRIC metric, string key)
    {
        if (!_metrics.TryGetValue(metric, out var info))
            return;
        if (info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC && info.MetricType != PresentMonNative.PM_METRIC_TYPE.DYNAMIC_FRAME)
            return;

        foreach (var deviceId in ResolveGpuMetricDeviceIds(info))
        {
            var element = CreateElement(metric, deviceId, 0u);
            elements.Add(new QuerySpec(element, new Binding(BindingKind.GpuScalar, key, deviceId, 0, info.PolledType, info.Unit, element)));
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

    private double? GetScalar(string key) => _processValues.TryGetValue(key, out var value) && double.IsFinite(value) ? value : null;

    private double? GetRateMetric(string key, bool fallbackFromFrameTimes = false, bool fallbackFromDisplayedTime = false)
    {
        var direct = GetScalar(key);
        if (direct.HasValue)
            return direct;

        if (fallbackFromDisplayedTime)
        {
            var displayedFrameTime = GetScalar(DisplayedFrameTimeKey);
            if (displayedFrameTime is > 0 and < 10000)
                return 1000.0 / displayedFrameTime.Value;

            var betweenDisplayChanges = GetMetric(BetweenDisplayChangeKey);
            if (betweenDisplayChanges is > 0 and < 10000)
                return 1000.0 / betweenDisplayChanges.Value;
        }

        if (fallbackFromFrameTimes)
            return CalculateHistoryAverageFps();

        return null;
    }

    private double? GetMetric(string key) => _frameValues.TryGetValue(key, out var value) && double.IsFinite(value) ? value : null;

    private GpuSample? ChooseActiveGpu()
    {
        if (_gpuSamples.Count == 0)
            return null;

        var graphics = _gpuSamples.Values
            .Where(IsGraphicsDevice)
            .ToList();

        var candidates = graphics.Count > 0 ? graphics : _gpuSamples.Values.ToList();
        return candidates
            .OrderByDescending(x => x.UsagePercent ?? -1)
            .ThenByDescending(x => x.HasUsefulData ? 1 : 0)
            .FirstOrDefault();
    }

    private bool IsGraphicsDevice(GpuSample sample) =>
        _deviceTypes.TryGetValue(sample.DeviceId, out var type) && type == PresentMonNative.PM_DEVICE_TYPE.GRAPHICS_ADAPTER;

    private void AddFrameTime(double frameTimeMs)
    {
        if (_frameCount < MaxFrames)
        {
            _frameTimesMs[_frameHead] = frameTimeMs;
            _frameSumMs += frameTimeMs;
            _frameCount++;
        }
        else
        {
            _frameSumMs -= _frameTimesMs[_frameHead];
            _frameTimesMs[_frameHead] = frameTimeMs;
            _frameSumMs += frameTimeMs;
        }

        _frameHead++;
        if (_frameHead == MaxFrames)
            _frameHead = 0;
    }

    private void ResetFrameHistory()
    {
        Array.Clear(_frameTimesMs, 0, _frameTimesMs.Length);
        _frameHead = 0;
        _frameCount = 0;
        _frameSumMs = 0;
    }

    private int GetNewestFrameIndex() =>
        _frameCount == 0 ? -1 : (_frameHead - 1 + MaxFrames) % MaxFrames;

    private static int PreviousFrameIndex(int index) =>
        index == 0 ? MaxFrames - 1 : index - 1;

    private double? CalculateFps()
    {
        if (_frameCount == 0)
            return null;

        var elapsed = 0.0;
        var count = 0;
        var index = GetNewestFrameIndex();
        for (var i = 0; i < _frameCount && elapsed < 1000.0; i++)
        {
            elapsed += _frameTimesMs[index];
            count++;
            index = PreviousFrameIndex(index);
        }

        return elapsed > 0 && count > 0 ? 1000.0 * count / elapsed : null;
    }

    private double? CalculateAverageFps()
    {
        if (!_averageFpsMeasurementStarted || _averageFpsFrameCount <= 0)
            return null;

        var elapsedSeconds = _averageFpsStopwatch.Elapsed.TotalSeconds;
        return elapsedSeconds > 0 ? _averageFpsFrameCount / elapsedSeconds : null;
    }

    private double? CalculateHistoryAverageFps()
    {
        if (_frameCount == 0 || _frameSumMs <= 0)
            return null;

        return 1000.0 * _frameCount / _frameSumMs;
    }

    private void ResetAverageFpsMeasurement()
    {
        _averageFpsStopwatch.Reset();
        _averageFpsFrameCount = 0;
        _averageFpsMeasurementStarted = false;
    }

    private (double? low1, double? low01) CalculateLowFpsPair()
    {
        if (_frameCount == 0 || _frameSumMs <= 0)
            return (null, null);

        var index = (_frameHead - _frameCount + MaxFrames) % MaxFrames;
        for (var i = 0; i < _frameCount; i++)
        {
            _lowFpsScratch[i] = _frameTimesMs[index];
            index++;
            if (index == MaxFrames)
                index = 0;
        }

        Array.Sort(_lowFpsScratch, 0, _frameCount);

        return (
            LowFromSlowestFrames(0.01),
            LowFromSlowestFrames(0.001));
    }

    private double? LowFromSlowestFrames(double fraction)
    {
        if (_frameCount == 0 || !double.IsFinite(fraction) || fraction <= 0)
            return null;

        // Don't turn a percentile into a single-frame outlier when the history is too short
        // to contain even one frame in that percentile.
        var slowFrameCount = (int)Math.Floor(_frameCount * fraction);
        if (slowFrameCount == 0)
            return null;
        var totalFrameTimeMs = 0.0;
        for (var i = _frameCount - slowFrameCount; i < _frameCount; i++)
            totalFrameTimeMs += _lowFpsScratch[i];

        var averageFrameTimeMs = totalFrameTimeMs / slowFrameCount;
        return averageFrameTimeMs > 0 && double.IsFinite(averageFrameTimeMs)
            ? 1000.0 / averageFrameTimeMs
            : null;
    }

    private double? CalculateFrameTime()
    {
        if (_frameCount == 0)
            return null;

        var total = 0.0;
        var count = 0;
        var index = GetNewestFrameIndex();
        for (var i = 0; i < _frameCount && total < 1000.0; i++)
        {
            total += _frameTimesMs[index];
            count++;
            index = PreviousFrameIndex(index);
        }

        return count > 0 ? total / count : null;
    }

    private void StopTrackingQueriesOnly()
    {
        var api = _api;
        if (api is not null)
        {
            try { if (_dynamicQuery != 0) api.FreeDynamicQuery(_dynamicQuery); } catch { }
            try { if (_frameQuery != 0) api.FreeFrameQuery(_frameQuery); } catch { }
        }

        _dynamicQuery = 0;
        _frameQuery = 0;
        _dynamicBlobSize = 0;
        _frameBlobSize = 0;

        lock (_sync)
        {
            _bindings.Clear();
            _frameBindings.Clear();
            _processValues.Clear();
            _frameValues.Clear();
            _gpuSamples.Clear();
        }

        FreeBuffer(ref _dynamicBlob);
        FreeBuffer(ref _frameBlob);
    }

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
        internal string Key { get; }
        internal uint DeviceId { get; }
        internal int ArrayIndex { get; }
        internal PresentMonNative.PM_DATA_TYPE DataType { get; }
        internal PresentMonNative.PM_UNIT Unit { get; }
        internal PresentMonNative.PM_QUERY_ELEMENT Element { get; set; }

        internal Binding(BindingKind kind, string key, uint deviceId, int arrayIndex, PresentMonNative.PM_DATA_TYPE dataType, PresentMonNative.PM_UNIT unit, PresentMonNative.PM_QUERY_ELEMENT element)
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
        internal bool HasUsefulData => TemperatureC.HasValue || CoreClockMHz.HasValue || PowerW.HasValue || UsagePercent.HasValue || VramUsedMb.HasValue || RenderComputeUsagePercent.HasValue;

        internal GpuSample(uint deviceId) => DeviceId = deviceId;

        internal void Set(string key, double value)
        {
            switch (key)
            {
                case "temp": TemperatureC = value; break;
                case "core-clock": CoreClockMHz = value; break;
                case "mem-clock": MemoryClockMHz = value; break;
                case "vram": VramUsedMb = value; break;
                case GpuVramUtilizationKey:
                    VramUsagePercent = double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : null;
                    break;
                case "power": PowerW = value; break;
                case "usage": UsagePercent = value; break;
                case "render-compute": RenderComputeUsagePercent = value; break;
                case "power-limited": PowerLimited = value; break;
                case "temperature-limited": TemperatureLimited = value; break;
                case "current-limited": CurrentLimited = value; break;
                case "voltage-limited": VoltageLimited = value; break;
                case "utilization-limited": UtilizationLimited = value; break;
            }
        }
    }
}

public readonly record struct PresentMonFastFramePlan(
    bool Fps, bool GpuLatency, bool DisplayLatency, bool RenderPresentLatency, bool UntilDisplayed,
    bool BetweenPresents, bool BetweenDisplayChanges, bool ClickToPhotonLatency, bool AllInputToPhotonLatency)
{
    public bool HasAny => Fps || GpuLatency || DisplayLatency || RenderPresentLatency || UntilDisplayed ||
                           BetweenPresents || BetweenDisplayChanges || ClickToPhotonLatency || AllInputToPhotonLatency;
}

public readonly record struct PresentMonMetricPlan(
    bool Fps, bool AvgFps, bool Low1Fps, bool Low01Fps, bool FrameTime,
    bool DroppedFrames, bool PresentedFps, bool DisplayedFps, bool ApplicationFps,
    bool CpuUsage, bool CpuBusy, bool CpuWait, bool CpuFrameTime,
    bool GpuTemperature, bool GpuCoreClock, bool GpuMemoryClock, bool GpuVramUsage, bool VramUsagePercent,
    bool GpuPower, bool GpuUsage, bool GpuRenderCompute, bool GpuPowerLimited, bool GpuTemperatureLimited,
    bool GpuCurrentLimited, bool GpuVoltageLimited, bool GpuUtilizationLimited, bool GpuBusy, bool GpuWait, bool GpuTime,
    bool GpuLatency, bool DisplayLatency, bool RenderPresentLatency, bool UntilDisplayed,
    bool BetweenPresents, bool BetweenDisplayChanges, bool ClickToPhotonLatency, bool AllInputToPhotonLatency)
{
    public bool HasGpuMetric => GpuTemperature || GpuCoreClock || GpuMemoryClock || GpuVramUsage || VramUsagePercent ||
                                 GpuPower || GpuUsage || GpuRenderCompute || GpuPowerLimited || GpuTemperatureLimited ||
                                 GpuCurrentLimited || GpuVoltageLimited || GpuUtilizationLimited;

    public bool RequiresFrameHistory => Fps || AvgFps || Low1Fps || Low01Fps || FrameTime || PresentedFps || ApplicationFps;

    public bool HasAny => RequiresFrameHistory || DroppedFrames || DisplayedFps || CpuUsage || CpuBusy || CpuWait ||
                          CpuFrameTime || HasGpuMetric || GpuBusy || GpuWait || GpuTime || GpuLatency || DisplayLatency ||
                          RenderPresentLatency || UntilDisplayed || BetweenPresents || BetweenDisplayChanges ||
                          ClickToPhotonLatency || AllInputToPhotonLatency;
}

public sealed record PresentMonFastFrameSnapshot(
    double? Fps,
    double? GpuLatencyMs,
    double? DisplayLatencyMs,
    double? RenderPresentLatencyMs,
    double? UntilDisplayedMs,
    double? BetweenPresentsMs,
    double? BetweenDisplayChangeMs,
    double? ClickToPhotonLatencyMs,
    double? AllInputToPhotonLatencyMs);

public sealed record PresentMonSnapshot(
    IReadOnlyList<CpuCoreFrequency> CpuCoreFrequenciesMHz,
    double? CpuUsagePercent,
    double? GpuTemperatureC,
    double? GpuCoreClockMHz,
    double? GpuMemoryClockMHz,
    double? GpuVramUsedMb,
    double? GpuVramUsagePercent,
    double? GpuPowerWatts,
    double? GpuUsagePercent,
    double? GpuRenderComputeUsagePercent,
    double? GpuPowerLimited,
    double? GpuTemperatureLimited,
    double? GpuCurrentLimited,
    double? GpuVoltageLimited,
    double? GpuUtilizationLimited,
    double? Fps,
    double? AvgFps,
    double? Low1Fps,
    double? Low01Fps,
    double? FrameTimeMs,
    double? CpuFrameTimeMs,
    double? CpuBusyMs,
    double? CpuWaitMs,
    double? PresentedFps,
    double? DisplayedFps,
    double? ApplicationFps,
    double? GpuTimeMs,
    double? GpuBusyMs,
    double? GpuWaitMs,
    double? DroppedFrames,
    double? GpuLatencyMs,
    double? DisplayLatencyMs,
    double? RenderPresentLatencyMs,
    double? UntilDisplayedMs,
    double? BetweenPresentsMs,
    double? BetweenDisplayChangeMs,
    double? ClickToPhotonLatencyMs,
    double? AllInputToPhotonLatencyMs,
    bool IsAvailable,
    string? Error);
