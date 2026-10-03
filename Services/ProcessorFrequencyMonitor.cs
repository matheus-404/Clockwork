using System.Runtime.InteropServices;

namespace Clockwork.Services;

/// <summary>
/// Persistent PDH counters for per-logical-processor frequency. The counters are created once
/// and sampled only while the corresponding option is enabled.
/// </summary>
public sealed class ProcessorFrequencyMonitor : IDisposable
{
    private const int ERROR_SUCCESS = 0;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;

    private const string FrequencyPath = @"\Processor Information(*)\Processor Frequency";
    private const string PerformancePath = @"\Processor Information(*)\% Processor Performance";

    private nint _query;
    private nint _frequencyCounter;
    private nint _performanceCounter;
    private nint _buffer;
    private uint _bufferBytes;
    private bool _initialized;
    private bool _disposed;

    // Each core is classified once, from its first few samples: if "Processor Frequency" stays
    // constant it is the nominal/base clock and is scaled by "% Processor Performance"; if it
    // moves it already reports the actual clock and is used as-is. The result is final, so a core
    // can neither flip-flop later nor be scaled twice. Until every core is classified Sample()
    // returns nothing (N/A) rather than a possibly wrong value; this only happens on the first
    // sampling after launch, a few hundred milliseconds at the 100 ms telemetry rate.
    private const int ClassificationSamples = 3;

    private readonly Dictionary<int, double> _lastNominal = new();
    private readonly Dictionary<int, int> _stableSamples = new();
    private readonly Dictionary<int, int> _sampleCounts = new();
    private readonly HashSet<int> _nominalCores = new();
    private readonly HashSet<int> _classifiedCores = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }


    private bool Initialize()
    {
        if (_initialized || _disposed)
            return _initialized;

        if (PdhOpenQueryW(null, 0, out _query) != ERROR_SUCCESS)
            return false;

        if (PdhAddEnglishCounterW(_query, FrequencyPath, 0, out _frequencyCounter) != ERROR_SUCCESS ||
            PdhAddEnglishCounterW(_query, PerformancePath, 0, out _performanceCounter) != ERROR_SUCCESS)
        {
            DisposeQuery();
            return false;
        }

        PdhCollectQueryData(_query);
        _initialized = true;
        return true;
    }

    public double[] Sample()
    {
        if (_disposed || !Initialize() || PdhCollectQueryData(_query) != ERROR_SUCCESS)
            return Array.Empty<double>();

        var frequencies = ReadCounterArray(_frequencyCounter);
        var performance = ReadCounterArray(_performanceCounter);
        if (frequencies.Count == 0)
            return Array.Empty<double>();

        var performanceByCore = performance.Count == 0
            ? new Dictionary<int, double>()
            : performance;

        var result = new SortedDictionary<int, double>();
        var classifying = false;
        foreach (var entry in frequencies)
        {
            var key = entry.Key;
            var value = entry.Value;
            if (performanceByCore.TryGetValue(key, out var percent) &&
                double.IsFinite(percent) && percent > 0)
            {
                if (!_classifiedCores.Contains(key))
                {
                    var samples = _sampleCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                    _sampleCounts[key] = samples;

                    if (_lastNominal.TryGetValue(key, out var previousNominal) &&
                        Math.Abs(previousNominal - value) <= Math.Max(1.0, previousNominal * 0.001))
                    {
                        _stableSamples[key] = _stableSamples.TryGetValue(key, out var stable) ? stable + 1 : 1;
                    }
                    else
                    {
                        _stableSamples[key] = 0;
                    }

                    _lastNominal[key] = value;

                    if (_stableSamples[key] >= ClassificationSamples - 1)
                    {
                        _nominalCores.Add(key);
                        _classifiedCores.Add(key);
                    }
                    else if (samples >= ClassificationSamples)
                    {
                        _classifiedCores.Add(key); // Varies: already the actual clock.
                    }
                    else
                    {
                        classifying = true;
                    }
                }

                if (_nominalCores.Contains(key) && value > 0)
                    value *= percent / 100.0;
            }

            if (double.IsFinite(value) && value > 0)
                result[key] = value;
        }

        if (classifying)
            return Array.Empty<double>();

        return result.Values.ToArray();
    }

    private Dictionary<int, double> ReadCounterArray(nint counter)
    {
        var result = new Dictionary<int, double>();
        if (counter == 0)
            return result;

        while (true)
        {
            uint bufferBytes = 0;
            uint itemCount = 0;
            var status = PdhGetFormattedCounterArrayW(
                counter,
                PDH_FMT_DOUBLE | PDH_FMT_NOCAP100,
                ref bufferBytes,
                ref itemCount,
                0);

            if (status != ERROR_SUCCESS && status != PDH_MORE_DATA)
                return result;

            if (bufferBytes == 0 || itemCount == 0)
                return result;

            EnsureBuffer(bufferBytes);
            var bufferBytesForCall = _bufferBytes;
            var countForCall = itemCount;
            status = PdhGetFormattedCounterArrayW(
                counter,
                PDH_FMT_DOUBLE | PDH_FMT_NOCAP100,
                ref bufferBytesForCall,
                ref countForCall,
                _buffer);

            if (status == PDH_MORE_DATA)
            {
                EnsureBuffer(bufferBytesForCall);
                continue;
            }

            if (status != ERROR_SUCCESS)
                return result;

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (var i = 0u; i < countForCall; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(IntPtr.Add(_buffer, checked((int)(i * (uint)itemSize))));
                if (item.Name == 0 || item.Value.CStatus != ERROR_SUCCESS || !double.IsFinite(item.Value.DoubleValue))
                    continue;

                var name = Marshal.PtrToStringUni(item.Name);
                if (!TryParseCoreKey(name, out var key))
                    continue;

                result[key] = item.Value.DoubleValue;
            }

            return result;
        }
    }

    private void EnsureBuffer(uint required)
    {
        if (_buffer != 0 && _bufferBytes >= required)
            return;

        if (_buffer != 0)
            Marshal.FreeHGlobal(_buffer);

        _buffer = Marshal.AllocHGlobal(checked((int)required));
        _bufferBytes = required;
    }

    private static bool TryParseCoreKey(string? instanceName, out int sortKey)
    {
        sortKey = 0;
        if (string.IsNullOrWhiteSpace(instanceName))
            return false;

        var comma = instanceName.IndexOf(',');
        if (comma <= 0 || comma >= instanceName.Length - 1)
            return false;

        if (!int.TryParse(instanceName.AsSpan(0, comma), out var node) ||
            !int.TryParse(instanceName.AsSpan(comma + 1), out var processor) ||
            node < 0 || processor < 0)
            return false;

        sortKey = checked(node * 1_000_000 + processor);
        return true;
    }

    private void DisposeQuery()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }

        _frequencyCounter = 0;
        _performanceCounter = 0;
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeQuery();

        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
            _bufferBytes = 0;
        }

        GC.SuppressFinalize(this);
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, ulong userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(nint query, string fullCounterPath, ulong userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(nint query);
}
