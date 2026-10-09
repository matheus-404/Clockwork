using Clockwork.Services;

namespace Clockwork.Overlay;

/// <summary>Turns sampled telemetry into the text shown by each overlay line.</summary>
internal static class OverlayStatFormatter
{
    private const string NotAvailable = "N/A";

    internal static string Format(
        string stat,
        GameInfo game,
        PresentMonSnapshot snapshot,
        (double usedGb, double totalGb, double percent)? ram,
        DateTime systemTime,
        TimeSpan sessionElapsed,
        Func<PresentMonNative.PM_METRIC, string?> metricStatus)
    {
        string Num(double? value, string format, string unit) =>
            value is { } v && double.IsFinite(v) ? v.ToString(format) + unit : NotAvailable;
        string Flag(double? value) =>
            value is { } v && double.IsFinite(v) ? (v != 0 ? "Yes" : "No") : NotAvailable;
        string PresentMonValue(double? value, PresentMonNative.PM_METRIC metric, string format, string unit) =>
            value is { } v && double.IsFinite(v) ? v.ToString(format) + unit : metricStatus(metric) ?? NotAvailable;
        string PresentMonFlag(double? value, PresentMonNative.PM_METRIC metric) =>
            value is { } v && double.IsFinite(v) ? (v != 0 ? "Yes" : "No") : metricStatus(metric) ?? NotAvailable;

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

            "RAM Usage" => ram is { } memory ? $"{memory.usedGb:0.0} / {memory.totalGb:0.0} GB" : NotAvailable,
            "RAM Usage (%)" => ram is { } memory ? $"{memory.percent:0}%" : NotAvailable,
            "Process/Game RAM Usage" => game.WorkingSetBytes is { } bytes ? $"{bytes / 1073741824.0:0.0} GB" : NotAvailable,

            "System Time" => systemTime.ToString("HH:mm"),
            "Session Playtime" => FormatPlaytime(sessionElapsed),
            _ => NotAvailable,
        };
    }

    private static string FormatPlaytime(TimeSpan elapsed)
    {
        var totalHours = (long)elapsed.TotalHours;
        return $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}