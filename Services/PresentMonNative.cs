using System.Runtime.InteropServices;

namespace Clockwork.Services;

/// <summary>
/// Minimal managed binding for the PresentMon API 2 C ABI.
/// The installed PresentMonAPI2.dll is loaded dynamically so Clockwork does not ship a
/// potentially incompatible copy of the middleware. The API version and service are checked
/// at runtime instead.
/// </summary>
internal static class PresentMonNative
{
    internal enum PM_STATUS
    {
        SUCCESS,
        FAILURE,
        BAD_ARGUMENT,
        BAD_HANDLE,
        SERVICE_ERROR,
        INVALID_ETL_FILE,
        INVALID_PID,
        ALREADY_TRACKING_PROCESS,
        UNABLE_TO_CREATE_NSM,
        INVALID_ADAPTER_ID,
        OUT_OF_RANGE,
        INSUFFICIENT_BUFFER,
        PIPE_ERROR,
        SESSION_NOT_OPEN,
        MIDDLEWARE_MISSING_PATH,
        NONEXISTENT_FILE_PATH,
        MIDDLEWARE_INVALID_SIGNATURE,
        MIDDLEWARE_MISSING_ENDPOINT,
        MIDDLEWARE_VERSION_LOW,
        MIDDLEWARE_VERSION_HIGH,
        MIDDLEWARE_SERVICE_MISMATCH,
        QUERY_MALFORMED,
        MODE_MISMATCH,
        FEATURE_DISABLED,
    }

    internal enum PM_METRIC
    {
        CPU_FRAME_TIME = 8,
        CPU_BUSY = 9,
        CPU_WAIT = 10,
        DISPLAYED_FPS = 11,
        PRESENTED_FPS = 12,
        GPU_TIME = 13,
        GPU_BUSY = 14,
        GPU_WAIT = 15,
        DROPPED_FRAMES = 16,
        GPU_LATENCY = 23,
        DISPLAY_LATENCY = 24,
        CLICK_TO_PHOTON_LATENCY = 25,
        GPU_POWER = 27,
        GPU_FREQUENCY = 29,
        GPU_TEMPERATURE = 30,
        GPU_UTILIZATION = 32,
        GPU_RENDER_COMPUTE_UTILIZATION = 33,
        GPU_POWER_LIMITED = 35,
        GPU_TEMPERATURE_LIMITED = 36,
        GPU_CURRENT_LIMITED = 37,
        GPU_VOLTAGE_LIMITED = 38,
        GPU_UTILIZATION_LIMITED = 39,
        GPU_MEM_SIZE = 45,
        GPU_MEM_USED = 46,
        GPU_MEM_UTILIZATION = 47,
        GPU_MEM_FREQUENCY = 42,
        CPU_UTILIZATION = 56,
        CPU_FREQUENCY = 60,
        APPLICATION_FPS = 62,
        ALL_INPUT_TO_PHOTON_LATENCY = 65,
        BETWEEN_PRESENTS = 78,
        BETWEEN_DISPLAY_CHANGE = 80,
        UNTIL_DISPLAYED = 81,
        RENDER_PRESENT_LATENCY = 82,
        DISPLAYED_FRAME_TIME = 85,
    }

    internal enum PM_METRIC_TYPE
    {
        DYNAMIC,
        STATIC,
        FRAME_EVENT,
        DYNAMIC_FRAME,
    }

    internal enum PM_DEVICE_VENDOR
    {
        INTEL,
        NVIDIA,
        AMD,
        UNKNOWN,
    }

    internal enum PM_UNIT
    {
        DIMENSIONLESS,
        RATIO,
        BOOLEAN,
        PERCENT,
        FPS,
        MICROSECONDS,
        MILLISECONDS,
        SECONDS,
        MINUTES,
        HOURS,
        MILLIWATTS,
        WATTS,
        KILOWATTS,
        VERTICAL_BLANKS,
        MILLIVOLTS,
        VOLTS,
        HERTZ,
        KILOHERTZ,
        MEGAHERTZ,
        GIGAHERTZ,
        CELSIUS,
        RPM,
        BITS_PER_SECOND,
        KILOBITS_PER_SECOND,
        MEGABITS_PER_SECOND,
        GIGABITS_PER_SECOND,
        BYTES,
        KILOBYTES,
        MEGABYTES,
        GIGABYTES,
        QPC,
    }

    internal enum PM_STAT
    {
        NONE,
        AVG,
        PERCENTILE_99,
        PERCENTILE_95,
        PERCENTILE_90,
        PERCENTILE_01,
        PERCENTILE_05,
        PERCENTILE_10,
        MAX,
        MIN,
        MID_POINT,
        MID_LERP,
        NEWEST_POINT,
        OLDEST_POINT,
        COUNT,
        NON_ZERO_AVG,
    }

    internal enum PM_DATA_TYPE
    {
        DOUBLE,
        INT32,
        UINT32,
        ENUM,
        STRING,
        UINT64,
        BOOL,
        VOID,
    }

    internal enum PM_DEVICE_TYPE
    {
        INDEPENDENT,
        GRAPHICS_ADAPTER,
        SYSTEM,
    }

    internal enum PM_METRIC_AVAILABILITY
    {
        AVAILABLE,
        UNAVAILABLE,
        NOT_EXPORTED_BY_SOURCE,
        NOT_SUPPORTED_BY_DEVICE,
        NOT_IMPLEMENTED_BY_PRESENTMON,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_STRING
    {
        public IntPtr pData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_OBJARRAY
    {
        public IntPtr pData;
        public nuint size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_DEVICE
    {
        public uint id;
        public PM_DEVICE_TYPE type;
        public PM_DEVICE_VENDOR vendor;
        public IntPtr pName;
        public IntPtr pLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_DEVICE_METRIC_INFO
    {
        public uint deviceId;
        public PM_METRIC_AVAILABILITY availability;
        public uint arraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_DATA_TYPE_INFO
    {
        public PM_DATA_TYPE polledType;
        public PM_DATA_TYPE frameType;
        public int enumId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_STAT_INFO
    {
        public PM_STAT stat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_METRIC
    {
        public PM_METRIC id;
        public PM_METRIC_TYPE type;
        public PM_UNIT unit;
        public PM_UNIT preferredUnitHint;
        public IntPtr pTypeInfo;
        public IntPtr pStatInfo;
        public IntPtr pDeviceMetricInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_INTROSPECTION_ROOT
    {
        public IntPtr pMetrics;
        public IntPtr pEnums;
        public IntPtr pDevices;
        public IntPtr pUnits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PM_QUERY_ELEMENT
    {
        public PM_METRIC metric;
        public PM_STAT stat;
        public uint deviceId;
        public uint arrayIndex;
        public ulong dataOffset;
        public ulong dataSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct PM_VERSION
    {
        public ushort major;
        public ushort minor;
        public ushort patch;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 22, ArraySubType = UnmanagedType.I1)]
        public byte[] tag;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8, ArraySubType = UnmanagedType.I1)]
        public byte[] hash;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4, ArraySubType = UnmanagedType.I1)]
        public byte[] config;

        public static PM_VERSION CreateEmpty() => new()
        {
            tag = new byte[22],
            hash = new byte[8],
            config = new byte[4],
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmOpenSession(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmCloseSession(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmStartTrackingProcess(IntPtr handle, uint processId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmStopTrackingProcess(IntPtr handle, uint processId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmGetIntrospectionRoot(IntPtr handle, out IntPtr root);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmFreeIntrospectionRoot(IntPtr root);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmSetTelemetryPollingPeriod(IntPtr handle, uint reserved, uint timeMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmSetEtwFlushPeriod(IntPtr handle, uint periodMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmFlushFrames(IntPtr handle, uint processId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmRegisterDynamicQuery(IntPtr sessionHandle, out IntPtr queryHandle, [In, Out] PM_QUERY_ELEMENT[] elements, ulong numElements, double windowSizeMs, double metricOffsetMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmFreeDynamicQuery(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmPollDynamicQuery(IntPtr handle, uint processId, IntPtr pBlob, ref uint numSwapChains);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmPollStaticQuery(IntPtr sessionHandle, ref PM_QUERY_ELEMENT element, uint processId, IntPtr pBlob);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmRegisterFrameQuery(IntPtr sessionHandle, out IntPtr queryHandle, [In, Out] PM_QUERY_ELEMENT[] elements, ulong numElements, out uint blobSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmConsumeFrames(IntPtr handle, uint processId, IntPtr pBlobs, ref uint numFramesToRead);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmFreeFrameQuery(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate PM_STATUS PmGetApiVersion(out PM_VERSION version);

    internal sealed class Api : IDisposable
    {
        private IntPtr _module;
        private bool _disposed;

        internal PmOpenSession OpenSession { get; }
        internal PmCloseSession CloseSession { get; }
        internal PmStartTrackingProcess StartTrackingProcess { get; }
        internal PmStopTrackingProcess StopTrackingProcess { get; }
        internal PmGetIntrospectionRoot GetIntrospectionRoot { get; }
        internal PmFreeIntrospectionRoot FreeIntrospectionRoot { get; }
        internal PmSetTelemetryPollingPeriod SetTelemetryPollingPeriod { get; }
        internal PmSetEtwFlushPeriod SetEtwFlushPeriod { get; }
        internal PmFlushFrames FlushFrames { get; }
        internal PmRegisterDynamicQuery RegisterDynamicQuery { get; }
        internal PmFreeDynamicQuery FreeDynamicQuery { get; }
        internal PmPollDynamicQuery PollDynamicQuery { get; }
        internal PmPollStaticQuery PollStaticQuery { get; }
        internal PmRegisterFrameQuery RegisterFrameQuery { get; }
        internal PmConsumeFrames ConsumeFrames { get; }
        internal PmFreeFrameQuery FreeFrameQuery { get; }
        internal PmGetApiVersion GetApiVersion { get; }
        internal string LoadedPath { get; }

        private Api(IntPtr module, string loadedPath)
        {
            _module = module;
            LoadedPath = loadedPath;
            OpenSession = Load<PmOpenSession>("pmOpenSession");
            CloseSession = Load<PmCloseSession>("pmCloseSession");
            StartTrackingProcess = Load<PmStartTrackingProcess>("pmStartTrackingProcess");
            StopTrackingProcess = Load<PmStopTrackingProcess>("pmStopTrackingProcess");
            GetIntrospectionRoot = Load<PmGetIntrospectionRoot>("pmGetIntrospectionRoot");
            FreeIntrospectionRoot = Load<PmFreeIntrospectionRoot>("pmFreeIntrospectionRoot");
            SetTelemetryPollingPeriod = Load<PmSetTelemetryPollingPeriod>("pmSetTelemetryPollingPeriod");
            SetEtwFlushPeriod = Load<PmSetEtwFlushPeriod>("pmSetEtwFlushPeriod");
            FlushFrames = Load<PmFlushFrames>("pmFlushFrames");
            RegisterDynamicQuery = Load<PmRegisterDynamicQuery>("pmRegisterDynamicQuery");
            FreeDynamicQuery = Load<PmFreeDynamicQuery>("pmFreeDynamicQuery");
            PollDynamicQuery = Load<PmPollDynamicQuery>("pmPollDynamicQuery");
            PollStaticQuery = Load<PmPollStaticQuery>("pmPollStaticQuery");
            RegisterFrameQuery = Load<PmRegisterFrameQuery>("pmRegisterFrameQuery");
            ConsumeFrames = Load<PmConsumeFrames>("pmConsumeFrames");
            FreeFrameQuery = Load<PmFreeFrameQuery>("pmFreeFrameQuery");
            GetApiVersion = Load<PmGetApiVersion>("pmGetApiVersion");
        }

        internal static bool TryLoad(out Api? api, out string error)
        {
            api = null;
            error = string.Empty;

            foreach (var path in GetCandidatePaths())
            {
                if (!File.Exists(path))
                    continue;

                if (!NativeLibrary.TryLoad(path, out var module))
                    continue;

                try
                {
                    api = new Api(module, path);
                    return true;
                }
                catch (Exception ex)
                {
                    NativeLibrary.Free(module);
                    error = $"PresentMonAPI2.dll could not be loaded: {ex.Message}";
                }
            }

            error = string.IsNullOrWhiteSpace(error)
                ? "PresentMonAPI2.dll was not found. Install PresentMon Service 2.3.1 or newer."
                : error;
            return false;
        }

        private T Load<T>(string exportName) where T : Delegate
        {
            if (_module == 0 || _disposed)
                throw new ObjectDisposedException(nameof(Api));

            var address = NativeLibrary.GetExport(_module, exportName);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_module != 0)
            {
                NativeLibrary.Free(_module);
                _module = 0;
            }
            GC.SuppressFinalize(this);
        }
    }

    internal static IEnumerable<string> GetCandidatePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            Add(paths, Path.Combine(root, "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"));
            Add(paths, Path.Combine(root, "Intel", "PresentMon", "PresentMonAPI2.dll"));
            Add(paths, Path.Combine(root, "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"));
        }

        // The app folder comes last: Velopack installs into a user-writable directory, so a copy
        // there must never win over the installed, admin-protected PresentMon service files.
        Add(paths, Path.Combine(AppContext.BaseDirectory, "PresentMonAPI2.dll"));

        return paths;
    }

    private static void Add(HashSet<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }

    internal static string StatusText(PM_STATUS status) => status.ToString();
}
