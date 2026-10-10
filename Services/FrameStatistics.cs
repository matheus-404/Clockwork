using System.Diagnostics;

namespace Clockwork.Services;

/// <summary>
/// A ring buffer of recent frame times with timestamps. All "now" values are
/// <see cref="Stopwatch"/> ticks, which keeps the class deterministic and unit-testable.
/// Not thread-safe: callers serialise access.
/// </summary>
/// <remarks>
/// Every calculation treats the data as stale once the newest frame is older than
/// <see cref="StaleAfterMs"/>, so a paused game or loading screen reads as "no data" instead of
/// freezing on its last value.
/// </remarks>
public sealed class FrameStatistics
{
    /// <summary>The window used for the live FPS and frame-time readings.</summary>
    public const double FpsWindowMs = 1000.0;

    /// <summary>Data older than this is reported as missing.</summary>
    public const double StaleAfterMs = 2000.0;

    /// <summary>
    /// Frame times at or above this are treated as idle gaps (loading, pause, alt-tab) and are
    /// excluded from the running average FPS.
    /// </summary>
    public const double AverageGapThresholdMs = 2000.0;

    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private readonly double[] _frameTimesMs;
    private readonly long[] _stamps;
    private readonly int _capacity;
    private int _head;
    private int _count;

    private bool _averageActive;
    private long _averageFrames;
    private double _averageTimeMs;

    public FrameStatistics(int capacity = 65_536)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _frameTimesMs = new double[capacity];
        _stamps = new long[capacity];
    }

    public int Count => _count;

    public bool HasData => _count > 0;

    public void Reset()
    {
        _head = 0;
        _count = 0;
        _averageActive = false;
        _averageFrames = 0;
        _averageTimeMs = 0;
    }

    /// <summary>
    /// Turns the running average on or off. Any change restarts the measurement; calling it with
    /// the current state is a no-op, so toggling unrelated statistics never resets the average.
    /// </summary>
    public void SetAverageActive(bool active)
    {
        if (active == _averageActive)
            return;

        _averageActive = active;
        _averageFrames = 0;
        _averageTimeMs = 0;
    }

    /// <summary>
    /// Adds frames that were all consumed at <paramref name="nowTicks"/>. Timestamps are spread
    /// backwards from that moment using each frame's own duration, so a batch read after a long
    /// pause still lands at realistic times.
    /// </summary>
    public void AddBatch(ReadOnlySpan<double> frameTimesMs, long nowTicks)
    {
        if (frameTimesMs.IsEmpty)
            return;

        if (frameTimesMs.Length > _capacity)
            frameTimesMs = frameTimesMs[^_capacity..];

        var n = frameTimesMs.Length;
        var stamp = nowTicks;
        for (var i = n - 1; i >= 0; i--)
        {
            var position = _head + i;
            if (position >= _capacity)
                position -= _capacity;

            _frameTimesMs[position] = frameTimesMs[i];
            _stamps[position] = stamp;
            stamp -= (long)(frameTimesMs[i] * TicksPerMs);
        }

        _head = (_head + n) % _capacity;
        _count = Math.Min(_capacity, _count + n);

        if (_averageActive)
        {
            foreach (var frameMs in frameTimesMs)
            {
                if (frameMs > 0 && frameMs < AverageGapThresholdMs)
                {
                    _averageFrames++;
                    _averageTimeMs += frameMs;
                }
            }
        }
    }

    public void Add(double frameTimeMs, long nowTicks)
    {
        Span<double> single = stackalloc double[1];
        single[0] = frameTimeMs;
        AddBatch(single, nowTicks);
    }

    /// <summary>True when there is no data or the newest frame is older than <see cref="StaleAfterMs"/>.</summary>
    public bool IsStale(long nowTicks) =>
        _count == 0 || (nowTicks - NewestStamp) > StaleAfterMs * TicksPerMs;

    /// <summary>Frames per second over roughly the last second of frames, or null when stale.</summary>
    public double? CalculateFps(long nowTicks)
    {
        if (IsStale(nowTicks))
            return null;

        var elapsed = 0.0;
        var frames = 0;
        var index = NewestIndex;
        for (var i = 0; i < _count && elapsed < FpsWindowMs; i++)
        {
            elapsed += _frameTimesMs[index];
            frames++;
            index = Previous(index);
        }

        return elapsed > 0 && frames > 0 ? 1000.0 * frames / elapsed : null;
    }

    /// <summary>Average frame time over roughly the last second of frames, or null when stale.</summary>
    public double? CalculateFrameTime(long nowTicks)
    {
        if (IsStale(nowTicks))
            return null;

        var total = 0.0;
        var frames = 0;
        var index = NewestIndex;
        for (var i = 0; i < _count && total < FpsWindowMs; i++)
        {
            total += _frameTimesMs[index];
            frames++;
            index = Previous(index);
        }

        return frames > 0 ? total / frames : null;
    }

    /// <summary>Running average FPS since the average was turned on, ignoring idle gaps.</summary>
    public double? CalculateAverageFps() =>
        _averageFrames > 0 && _averageTimeMs > 0 ? 1000.0 * _averageFrames / _averageTimeMs : null;

    /// <summary>
    /// Copies the frame times that fall inside the last <paramref name="windowSeconds"/> into
    /// <paramref name="destination"/> (newest first) and returns how many were copied. Returns 0
    /// when the data is stale.
    /// </summary>
    public int CopyRecentFrameTimes(long nowTicks, double windowSeconds, double[] destination)
    {
        if (IsStale(nowTicks))
            return 0;

        var oldestAllowed = nowTicks - (long)(windowSeconds * Stopwatch.Frequency);
        var copied = 0;
        var index = NewestIndex;
        for (var i = 0; i < _count && copied < destination.Length; i++)
        {
            if (_stamps[index] < oldestAllowed)
                break;

            destination[copied++] = _frameTimesMs[index];
            index = Previous(index);
        }

        return copied;
    }

    /// <summary>
    /// Computes the 1% and 0.1% low FPS from frame times. Sorts <paramref name="samples"/> in place,
    /// so call it on a copy and outside any lock.
    /// </summary>
    public static (double? Low1, double? Low01) ComputeLows(double[] samples, int count)
    {
        if (count <= 0)
            return (null, null);

        Array.Sort(samples, 0, count);
        return (LowFromSlowest(samples, count, 0.01), LowFromSlowest(samples, count, 0.001));
    }

    private static double? LowFromSlowest(double[] sortedAscending, int count, double fraction)
    {
        var slowCount = (int)Math.Floor(count * fraction);
        if (slowCount == 0)
            return null;

        var total = 0.0;
        for (var i = count - slowCount; i < count; i++)
            total += sortedAscending[i];

        var average = total / slowCount;
        return average > 0 && double.IsFinite(average) ? 1000.0 / average : null;
    }

    private long NewestStamp => _count == 0 ? 0 : _stamps[NewestIndex];

    private int NewestIndex => _head == 0 ? _capacity - 1 : _head - 1;

    private int Previous(int index) => index == 0 ? _capacity - 1 : index - 1;
}
