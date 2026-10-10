using System.Diagnostics;
using Clockwork.Services;
using Xunit;

namespace Clockwork.Tests;

public class FrameStatisticsTests
{
    private static readonly long Second = Stopwatch.Frequency;

    private static FrameStatistics Steady(int fps, int seconds, long nowTicks, int capacity = 65_536)
    {
        var stats = new FrameStatistics(capacity);
        var frames = new double[fps * seconds];
        Array.Fill(frames, 1000.0 / fps);
        stats.AddBatch(frames, nowTicks);
        return stats;
    }

    [Fact]
    public void Fps_SteadyFrames_ReportsTheRate()
    {
        var now = 1000 * Second;
        var stats = Steady(60, 5, now);

        var fps = stats.CalculateFps(now);

        Assert.NotNull(fps);
        Assert.InRange(fps!.Value, 59.9, 60.1);
    }

    [Fact]
    public void FrameTime_SteadyFrames_ReportsTheFrameTime()
    {
        var now = 1000 * Second;
        var stats = Steady(100, 3, now);

        var frameTime = stats.CalculateFrameTime(now);

        Assert.NotNull(frameTime);
        Assert.InRange(frameTime!.Value, 9.99, 10.01);
    }

    [Fact]
    public void Readings_BecomeNull_OnceTheNewestFrameIsStale()
    {
        var now = 1000 * Second;
        var stats = Steady(60, 5, now);

        // Still fresh 1.5 s later, gone after 3 s: a paused game must not freeze on its last value.
        Assert.NotNull(stats.CalculateFps(now + (long)(1.5 * Second)));
        Assert.Null(stats.CalculateFps(now + 3 * Second));
        Assert.Null(stats.CalculateFrameTime(now + 3 * Second));
        Assert.True(stats.IsStale(now + 3 * Second));
    }

    [Fact]
    public void EmptyStatistics_AreStaleAndReportNothing()
    {
        var stats = new FrameStatistics();

        Assert.True(stats.IsStale(5 * Second));
        Assert.Null(stats.CalculateFps(5 * Second));
        Assert.Null(stats.CalculateFrameTime(5 * Second));
        Assert.Null(stats.CalculateAverageFps());
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var now = 1000 * Second;
        var stats = Steady(60, 2, now);
        stats.SetAverageActive(true);

        stats.Reset();

        Assert.Equal(0, stats.Count);
        Assert.True(stats.IsStale(now));
        Assert.Null(stats.CalculateAverageFps());
    }

    [Fact]
    public void RingBuffer_KeepsOnlyTheNewestFrames()
    {
        var stats = new FrameStatistics(8);
        var frames = Enumerable.Range(1, 20).Select(static i => (double)i).ToArray();
        var now = 1000 * Second;

        stats.AddBatch(frames, now);

        Assert.Equal(8, stats.Count);

        // The newest 8 frames are 13..20 (mean 16.5).
        var frameTime = stats.CalculateFrameTime(now);
        Assert.NotNull(frameTime);
        Assert.Equal(16.5, frameTime!.Value, 3);
    }

    [Fact]
    public void Lows_UseTheSlowestFrames()
    {
        var now = 1000 * Second;
        var stats = new FrameStatistics();
        var frames = new double[100];
        Array.Fill(frames, 10.0);
        frames[40] = 100.0;
        stats.AddBatch(frames, now);

        var buffer = new double[128];
        var copied = stats.CopyRecentFrameTimes(now, 60, buffer);
        var (low1, low01) = FrameStatistics.ComputeLows(buffer, copied);

        Assert.Equal(100, copied);
        Assert.NotNull(low1);
        Assert.Equal(10.0, low1!.Value, 3);   // worst 1% of 100 frames is the single 100 ms frame
        Assert.Null(low01);                   // needs at least 1000 frames
    }

    [Fact]
    public void Lows_NeedEnoughFramesForTheLowerPercentile()
    {
        var now = 1000 * Second;
        var stats = new FrameStatistics();
        var frames = new double[2000];
        Array.Fill(frames, 10.0);
        frames[5] = 200.0;
        stats.AddBatch(frames, now);

        var buffer = new double[4096];
        var copied = stats.CopyRecentFrameTimes(now, 60, buffer);
        var (low1, low01) = FrameStatistics.ComputeLows(buffer, copied);

        Assert.NotNull(low1);
        Assert.NotNull(low01);
        Assert.True(low01!.Value <= low1!.Value);
    }

    [Fact]
    public void CopyRecentFrameTimes_HonoursTheTimeWindow()
    {
        var start = 1000 * Second;
        var stats = new FrameStatistics();
        stats.AddBatch(new[] { 10.0, 10.0, 10.0 }, start);
        stats.AddBatch(new[] { 10.0, 10.0, 10.0, 10.0, 10.0 }, start + 120 * Second);

        var buffer = new double[16];
        var copied = stats.CopyRecentFrameTimes(start + 120 * Second, 60, buffer);

        Assert.Equal(5, copied);
    }

    [Fact]
    public void CopyRecentFrameTimes_ReturnsNothingWhenStale()
    {
        var now = 1000 * Second;
        var stats = Steady(60, 2, now);

        Assert.Equal(0, stats.CopyRecentFrameTimes(now + 10 * Second, 60, new double[256]));
    }

    [Fact]
    public void AverageFps_IgnoresIdleGaps()
    {
        var now = 1000 * Second;
        var stats = new FrameStatistics();
        stats.SetAverageActive(true);

        var frames = new List<double>();
        frames.AddRange(Enumerable.Repeat(1000.0 / 60, 60));
        frames.Add(5000.0); // loading screen / alt-tab: excluded
        frames.AddRange(Enumerable.Repeat(1000.0 / 60, 60));
        stats.AddBatch(frames.ToArray(), now);

        var average = stats.CalculateAverageFps();

        Assert.NotNull(average);
        Assert.InRange(average!.Value, 59.9, 60.1);
    }

    [Fact]
    public void AverageFps_OnlyCountsFramesAddedWhileActive()
    {
        var now = 1000 * Second;
        var stats = new FrameStatistics();

        stats.AddBatch(Enumerable.Repeat(10.0, 100).ToArray(), now);
        Assert.Null(stats.CalculateAverageFps());

        stats.SetAverageActive(true);
        stats.AddBatch(Enumerable.Repeat(20.0, 50).ToArray(), now + Second);

        var average = stats.CalculateAverageFps();
        Assert.NotNull(average);
        Assert.Equal(50.0, average!.Value, 3);
    }

    [Fact]
    public void SetAverageActive_WithTheCurrentStateDoesNotRestartTheAverage()
    {
        var now = 1000 * Second;
        var stats = new FrameStatistics();
        stats.SetAverageActive(true);
        stats.AddBatch(Enumerable.Repeat(10.0, 100).ToArray(), now);

        stats.SetAverageActive(true); // e.g. an unrelated statistic was toggled

        Assert.NotNull(stats.CalculateAverageFps());

        stats.SetAverageActive(false);
        Assert.Null(stats.CalculateAverageFps());
    }

    [Fact]
    public void BatchTimestamps_AreSpreadBackwardsFromTheConsumptionTime()
    {
        var now = 1000 * Second;
        var stats = new FrameStatistics();

        // Ten 100 ms frames consumed at once cover the last second: all of them fall inside a
        // 1.5 s window, but only the newest ones fall inside a 0.5 s window.
        stats.AddBatch(Enumerable.Repeat(100.0, 10).ToArray(), now);

        Assert.Equal(10, stats.CopyRecentFrameTimes(now, 1.5, new double[32]));
        Assert.True(stats.CopyRecentFrameTimes(now, 0.5, new double[32]) < 10);
    }
}
