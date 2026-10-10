using System.Numerics;

namespace Clockwork.Overlay;

/// <summary>
/// Every statistic Clockwork can show. The numeric values are used as bit positions in
/// <see cref="StatSet"/> (so there can be at most 64) and as indexes into value arrays.
/// The declaration order is also the order lines appear on the overlay.
/// </summary>
public enum StatId
{
    // Performance
    Fps, AvgFps, Low1Fps, Low01Fps, FrameTime, DroppedFrames, PresentedFps, DisplayedFps, ApplicationFps,

    // CPU
    CpuUsage, CpuBusy, CpuWait, CpuFrameTime,

    // GPU
    GpuTemperature, GpuCoreClock, GpuMemoryClock, GpuVramUsage, GpuVramPercent, GpuPower, GpuUsage,
    GpuRenderCompute, GpuPowerLimited, GpuTemperatureLimited, GpuCurrentLimited, GpuVoltageLimited,
    GpuUtilizationLimited, GpuBusy, GpuWait, GpuTime,

    // RAM
    RamUsage, RamPercent, ProcessRam,

    // Latency
    GpuLatency, DisplayLatency, RenderPresentLatency, UntilDisplayed, BetweenPresents,
    BetweenDisplayChanges, ClickToPhotonLatency, AllInputToPhotonLatency,

    // More
    SystemTime, SessionPlaytime,
}

/// <summary>How a statistic's value is produced and turned into text.</summary>
public enum StatKind
{
    /// <summary>A PresentMon number formatted with decimals and a unit.</summary>
    Number,

    /// <summary>A PresentMon value shown as Yes/No.</summary>
    Flag,

    /// <summary>System RAM as "used / total GB".</summary>
    Ram,

    /// <summary>System RAM as a percentage.</summary>
    RamPercent,

    /// <summary>The game's private working set.</summary>
    ProcessRam,

    /// <summary>Local time as HH:mm.</summary>
    SystemTime,

    /// <summary>Time since the tracked game was first seen.</summary>
    Playtime,
}

/// <summary>An immutable set of <see cref="StatId"/> values stored in one machine word.</summary>
public readonly struct StatSet : IEquatable<StatSet>
{
    public StatSet(ulong bits) => Bits = bits;

    public ulong Bits { get; }

    public static StatSet Empty => default;

    public bool IsEmpty => Bits == 0;

    public int Count => BitOperations.PopCount(Bits);

    public static StatSet Of(params StatId[] ids)
    {
        ulong bits = 0;
        foreach (var id in ids)
            bits |= 1UL << (int)id;
        return new StatSet(bits);
    }

    public bool Contains(StatId id) => (Bits & (1UL << (int)id)) != 0;

    public bool Intersects(StatSet other) => (Bits & other.Bits) != 0;

    public StatSet With(StatId id) => new(Bits | (1UL << (int)id));

    public StatId[] ToArray()
    {
        var result = new StatId[Count];
        var index = 0;
        for (var bits = Bits; bits != 0; bits &= bits - 1)
            result[index++] = (StatId)BitOperations.TrailingZeroCount(bits);
        return result;
    }

    public static StatSet operator |(StatSet left, StatSet right) => new(left.Bits | right.Bits);

    public static StatSet operator &(StatSet left, StatSet right) => new(left.Bits & right.Bits);

    public static StatSet operator -(StatSet left, StatSet right) => new(left.Bits & ~right.Bits);

    public static bool operator ==(StatSet left, StatSet right) => left.Bits == right.Bits;

    public static bool operator !=(StatSet left, StatSet right) => left.Bits != right.Bits;

    public bool Equals(StatSet other) => Bits == other.Bits;

    public override bool Equals(object? obj) => obj is StatSet other && Equals(other);

    public override int GetHashCode() => Bits.GetHashCode();
}

/// <summary>Static description of one statistic.</summary>
/// <param name="Id">Identifier and bit position.</param>
/// <param name="Key">Stable key used when persisting settings. Never change an existing key.</param>
/// <param name="Section">Settings section the statistic is shown in.</param>
/// <param name="Label">Text shown in the settings window and as the overlay line label.</param>
/// <param name="IconFile">Icon file name under Assets/Icons.</param>
/// <param name="Kind">How the value is produced and formatted.</param>
/// <param name="Decimals">Decimal places for <see cref="StatKind.Number"/>.</param>
/// <param name="Unit">Suffix for <see cref="StatKind.Number"/>.</param>
/// <param name="DefaultOn">Whether the statistic is on by default.</param>
public sealed record StatDefinition(
    StatId Id,
    string Key,
    string Section,
    string Label,
    string IconFile,
    StatKind Kind,
    int Decimals = 0,
    string Unit = "",
    bool DefaultOn = false)
{
    /// <summary>The key older versions used ("Section/Label"), kept so saved settings migrate.</summary>
    public string LegacyKey => $"{Section}/{Label}";
}

/// <summary>A settings section and the statistics in it.</summary>
public sealed record StatSectionDefinition(
    string Name,
    string IconFile,
    string Description,
    IReadOnlyList<StatDefinition> Stats);

/// <summary>Frequently needed groupings of statistics.</summary>
public static class StatGroups
{
    /// <summary>Statistics whose values come from PresentMon.</summary>
    public static readonly StatSet PresentMonBacked = StatSet.Of(
        StatId.Fps, StatId.AvgFps, StatId.Low1Fps, StatId.Low01Fps, StatId.FrameTime, StatId.DroppedFrames,
        StatId.PresentedFps, StatId.DisplayedFps, StatId.ApplicationFps,
        StatId.CpuUsage, StatId.CpuBusy, StatId.CpuWait, StatId.CpuFrameTime,
        StatId.GpuTemperature, StatId.GpuCoreClock, StatId.GpuMemoryClock, StatId.GpuVramUsage,
        StatId.GpuVramPercent, StatId.GpuPower, StatId.GpuUsage, StatId.GpuRenderCompute,
        StatId.GpuPowerLimited, StatId.GpuTemperatureLimited, StatId.GpuCurrentLimited,
        StatId.GpuVoltageLimited, StatId.GpuUtilizationLimited, StatId.GpuBusy, StatId.GpuWait, StatId.GpuTime,
        StatId.GpuLatency, StatId.DisplayLatency, StatId.RenderPresentLatency, StatId.UntilDisplayed,
        StatId.BetweenPresents, StatId.BetweenDisplayChanges, StatId.ClickToPhotonLatency,
        StatId.AllInputToPhotonLatency);

    /// <summary>Statistics refreshed by the fast loop (FPS and per-frame latency values).</summary>
    public static readonly StatSet FastOwned = StatSet.Of(
        StatId.Fps, StatId.GpuLatency, StatId.DisplayLatency, StatId.RenderPresentLatency,
        StatId.UntilDisplayed, StatId.BetweenPresents, StatId.BetweenDisplayChanges,
        StatId.ClickToPhotonLatency, StatId.AllInputToPhotonLatency);

    /// <summary>Statistics read from the polled GPU telemetry.</summary>
    public static readonly StatSet Gpu = StatSet.Of(
        StatId.GpuTemperature, StatId.GpuCoreClock, StatId.GpuMemoryClock, StatId.GpuVramUsage,
        StatId.GpuVramPercent, StatId.GpuPower, StatId.GpuUsage, StatId.GpuRenderCompute,
        StatId.GpuPowerLimited, StatId.GpuTemperatureLimited, StatId.GpuCurrentLimited,
        StatId.GpuVoltageLimited, StatId.GpuUtilizationLimited);

    /// <summary>Statistics that need the per-frame time history.</summary>
    public static readonly StatSet FrameHistory = StatSet.Of(
        StatId.Fps, StatId.AvgFps, StatId.Low1Fps, StatId.Low01Fps, StatId.FrameTime,
        StatId.PresentedFps, StatId.ApplicationFps);
}

/// <summary>The single source of truth for which statistics exist.</summary>
public static class StatRegistry
{
    private static readonly StatDefinition[] s_all = Build();
    private static readonly StatDefinition[] s_byId = IndexById(s_all);
    private static readonly IReadOnlyList<StatSectionDefinition> s_sections = BuildSections(s_all);

    /// <summary>All statistics, in settings/overlay order.</summary>
    public static IReadOnlyList<StatDefinition> All => s_all;

    public static IReadOnlyList<StatSectionDefinition> Sections => s_sections;

    public static int Count => s_all.Length;

    public static StatDefinition Get(StatId id) => s_byId[(int)id];

    /// <summary>
    /// Looks up a saved on/off state, preferring the stable key and falling back to the
    /// "Section/Label" key written by earlier versions.
    /// </summary>
    public static bool TryResolveSavedState(IReadOnlyDictionary<string, bool> saved, StatDefinition definition, out bool isOn)
    {
        if (saved.TryGetValue(definition.Key, out isOn))
            return true;

        return saved.TryGetValue(definition.LegacyKey, out isOn);
    }

    private static StatDefinition[] Build()
    {
        const string perf = "Performance";
        const string cpu = "CPU";
        const string gpu = "GPU";
        const string ram = "RAM";
        const string latency = "Latency";
        const string more = "More";

        return
        [
            new(StatId.Fps, "fps", perf, "FPS", "Monitor.svg", StatKind.Number, 0, "", DefaultOn: true),
            new(StatId.AvgFps, "avg-fps", perf, "Avg FPS", "Pulse.svg", StatKind.Number),
            new(StatId.Low1Fps, "low-1-fps", perf, "1% Low FPS", "Pulse.svg", StatKind.Number),
            new(StatId.Low01Fps, "low-01-fps", perf, "0.1% Low FPS", "Pulse.svg", StatKind.Number),
            new(StatId.FrameTime, "frame-time", perf, "Frame Time", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.DroppedFrames, "dropped-frames", perf, "Dropped Frames", "Pulse.svg", StatKind.Flag),
            new(StatId.PresentedFps, "presented-fps", perf, "Presented FPS", "Monitor.svg", StatKind.Number),
            new(StatId.DisplayedFps, "displayed-fps", perf, "Displayed FPS", "Monitor.svg", StatKind.Number),
            new(StatId.ApplicationFps, "application-fps", perf, "Application FPS", "Monitor.svg", StatKind.Number),

            new(StatId.CpuUsage, "cpu-usage", cpu, "CPU Usage", "Pulse.svg", StatKind.Number, 0, "%"),
            new(StatId.CpuBusy, "cpu-busy", cpu, "CPU Busy", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.CpuWait, "cpu-wait", cpu, "CPU Wait", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.CpuFrameTime, "cpu-frame-time", cpu, "CPU Frame Time", "Clock.svg", StatKind.Number, 1, " ms"),

            new(StatId.GpuTemperature, "gpu-temperature", gpu, "GPU Temperature", "Thermometer.svg", StatKind.Number, 0, "°C"),
            new(StatId.GpuCoreClock, "gpu-core-clock", gpu, "GPU Core Clock Frequency", "Gauge.svg", StatKind.Number, 0, " MHz"),
            new(StatId.GpuMemoryClock, "gpu-memory-clock", gpu, "GPU Memory Clock Frequency", "Gauge.svg", StatKind.Number, 0, " MHz"),
            new(StatId.GpuVramUsage, "gpu-vram-usage", gpu, "GPU VRAM Usage", "RAM.svg", StatKind.Number, 0, " MB"),
            new(StatId.GpuVramPercent, "gpu-vram-percent", gpu, "VRAM Usage (%)", "Percent.svg", StatKind.Number, 0, "%"),
            new(StatId.GpuPower, "gpu-power", gpu, "GPU Power", "Plug.svg", StatKind.Number, 0, " W"),
            new(StatId.GpuUsage, "gpu-usage", gpu, "GPU Usage", "Pulse.svg", StatKind.Number, 0, "%"),
            new(StatId.GpuRenderCompute, "gpu-render-compute", gpu, "GPU Render/Compute Utilization", "Pulse.svg", StatKind.Number, 0, "%"),
            new(StatId.GpuPowerLimited, "gpu-power-limited", gpu, "GPU Power Limited", "Pulse.svg", StatKind.Flag),
            new(StatId.GpuTemperatureLimited, "gpu-temperature-limited", gpu, "GPU Temperature Limited", "Thermometer.svg", StatKind.Flag),
            new(StatId.GpuCurrentLimited, "gpu-current-limited", gpu, "GPU Current Limited", "Pulse.svg", StatKind.Flag),
            new(StatId.GpuVoltageLimited, "gpu-voltage-limited", gpu, "GPU Voltage Limited", "Pulse.svg", StatKind.Flag),
            new(StatId.GpuUtilizationLimited, "gpu-utilization-limited", gpu, "GPU Utilization Limited", "Pulse.svg", StatKind.Flag),
            new(StatId.GpuBusy, "gpu-busy", gpu, "GPU Busy", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.GpuWait, "gpu-wait", gpu, "GPU Wait", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.GpuTime, "gpu-time", gpu, "GPU Time", "Clock.svg", StatKind.Number, 1, " ms"),

            new(StatId.RamUsage, "ram-usage", ram, "RAM Usage", "RAM.svg", StatKind.Ram),
            new(StatId.RamPercent, "ram-percent", ram, "RAM Usage (%)", "Percent.svg", StatKind.RamPercent),
            new(StatId.ProcessRam, "process-ram", ram, "Process/Game RAM Usage", "Monitor.svg", StatKind.ProcessRam),

            new(StatId.GpuLatency, "gpu-latency", latency, "GPU Latency", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.DisplayLatency, "display-latency", latency, "Display Latency", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.RenderPresentLatency, "render-present-latency", latency, "Render/Present Latency", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.UntilDisplayed, "until-displayed", latency, "Time Until Displayed", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.BetweenPresents, "between-presents", latency, "Between Presents", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.BetweenDisplayChanges, "between-display-changes", latency, "Between Display Changes", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.ClickToPhotonLatency, "click-to-photon", latency, "Click-to-Photon Latency", "Clock.svg", StatKind.Number, 1, " ms"),
            new(StatId.AllInputToPhotonLatency, "all-input-to-photon", latency, "All Input-to-Photon Latency", "Clock.svg", StatKind.Number, 1, " ms"),

            new(StatId.SystemTime, "system-time", more, "System Time", "Clock.svg", StatKind.SystemTime),
            new(StatId.SessionPlaytime, "session-playtime", more, "Session Playtime", "Timer.svg", StatKind.Playtime),
        ];
    }

    private static StatDefinition[] IndexById(StatDefinition[] all)
    {
        var byId = new StatDefinition[all.Length];
        foreach (var definition in all)
            byId[(int)definition.Id] = definition;
        return byId;
    }

    private static IReadOnlyList<StatSectionDefinition> BuildSections(StatDefinition[] all)
    {
        var descriptions = new Dictionary<string, (string Icon, string Description)>
        {
            ["Performance"] = ("Pulse.svg", "Select which performance information you want to display in-game."),
            ["CPU"] = ("CPU.svg", "Select which CPU information you want to display in-game."),
            ["GPU"] = ("GPU.svg", "Select which GPU information you want to display in-game."),
            ["RAM"] = ("RAM.svg", "Select which RAM information you want to display in-game."),
            ["Latency"] = ("Clock.svg", "Select which latency information you want to display in-game."),
            ["More"] = ("More.svg", "Select which additional information you want to display in-game."),
        };

        var sections = new List<StatSectionDefinition>();
        foreach (var group in all.GroupBy(static d => d.Section))
        {
            var (icon, description) = descriptions[group.Key];
            sections.Add(new StatSectionDefinition(group.Key, icon, description, group.ToArray()));
        }

        return sections;
    }
}
