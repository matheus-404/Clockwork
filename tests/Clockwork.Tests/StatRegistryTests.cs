using Clockwork.Overlay;
using Xunit;

namespace Clockwork.Tests;

public class StatRegistryTests
{
    [Fact]
    public void EveryStatId_HasExactlyOneDefinition()
    {
        var ids = Enum.GetValues<StatId>();

        Assert.Equal(ids.Length, StatRegistry.Count);
        Assert.True(ids.Length <= 64, "StatSet stores ids as bits of a 64-bit word.");

        foreach (var id in ids)
            Assert.Equal(id, StatRegistry.Get(id).Id);
    }

    [Fact]
    public void Keys_AreUnique_AndLegacyKeysToo()
    {
        Assert.Equal(StatRegistry.All.Count, StatRegistry.All.Select(static d => d.Key).Distinct().Count());
        Assert.Equal(StatRegistry.All.Count, StatRegistry.All.Select(static d => d.LegacyKey).Distinct().Count());
        Assert.Equal(StatRegistry.All.Count, StatRegistry.All.Select(static d => d.Label).Distinct().Count());
    }

    [Fact]
    public void LegacyKeys_MatchWhatEarlierVersionsWrote()
    {
        Assert.Equal("Performance/FPS", StatRegistry.Get(StatId.Fps).LegacyKey);
        Assert.Equal("GPU/GPU Busy", StatRegistry.Get(StatId.GpuBusy).LegacyKey);
        Assert.Equal("Latency/GPU Latency", StatRegistry.Get(StatId.GpuLatency).LegacyKey);
        Assert.Equal("Latency/Input-to-Photon Latency", StatRegistry.Get(StatId.InputToPhotonLatency).LegacyKey);
        Assert.Equal("RAM/Process/Game RAM Usage", StatRegistry.Get(StatId.ProcessRam).LegacyKey);
        Assert.Equal("More/Session Playtime", StatRegistry.Get(StatId.SessionPlaytime).LegacyKey);
    }

    [Fact]
    public void SavedState_MigratesPreviousInputLatencyKeys()
    {
        var stat = StatRegistry.Get(StatId.InputToPhotonLatency);

        // Previous Clockwork releases using "all-input-to-photon" or "click-to-photon"
        var oldAllInput = new Dictionary<string, bool> { ["all-input-to-photon"] = true };
        var oldClick = new Dictionary<string, bool> { ["click-to-photon"] = true };

        Assert.True(StatRegistry.TryResolveSavedState(oldAllInput, stat, out var fromAllInput));
        Assert.True(fromAllInput);

        Assert.True(StatRegistry.TryResolveSavedState(oldClick, stat, out var fromClick));
        Assert.True(fromClick);
    }

    [Fact]
    public void Sections_AreInTheExpectedOrder()
    {
        Assert.Equal(
            new[] { "Performance", "CPU", "GPU", "RAM", "Latency", "More" },
            StatRegistry.Sections.Select(static s => s.Name).ToArray());

        Assert.Equal(StatRegistry.Count, StatRegistry.Sections.Sum(static s => s.Stats.Count));
    }

    [Fact]
    public void OnlyFpsIsOnByDefault()
    {
        var onByDefault = StatRegistry.All.Where(static d => d.DefaultOn).Select(static d => d.Id).ToArray();

        Assert.Equal(new[] { StatId.Fps }, onByDefault);
    }

    [Fact]
    public void SavedState_PrefersTheStableKey_AndFallsBackToTheLegacyKey()
    {
        var fps = StatRegistry.Get(StatId.Fps);
        var legacyOnly = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [fps.LegacyKey] = true,
        };
        var both = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [fps.LegacyKey] = true,
            [fps.Key] = false,
        };
        var neither = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        Assert.True(StatRegistry.TryResolveSavedState(legacyOnly, fps, out var fromLegacy));
        Assert.True(fromLegacy);

        Assert.True(StatRegistry.TryResolveSavedState(both, fps, out var fromStable));
        Assert.False(fromStable);

        Assert.False(StatRegistry.TryResolveSavedState(neither, fps, out _));
    }

    [Fact]
    public void StatSet_SupportsBasicSetOperations()
    {
        var a = StatSet.Of(StatId.Fps, StatId.CpuUsage);
        var b = StatSet.Of(StatId.CpuUsage, StatId.GpuBusy);

        Assert.True(a.Contains(StatId.Fps));
        Assert.False(a.Contains(StatId.GpuBusy));
        Assert.Equal(2, a.Count);
        Assert.True(a.Intersects(b));
        Assert.Equal(StatSet.Of(StatId.CpuUsage), a & b);
        Assert.Equal(3, (a | b).Count);
        Assert.Equal(StatSet.Of(StatId.Fps), a - b);
        Assert.True(StatSet.Empty.IsEmpty);
        Assert.Equal(a, StatSet.Empty.With(StatId.Fps).With(StatId.CpuUsage));
        Assert.Equal(new[] { StatId.Fps, StatId.CpuUsage }, a.ToArray());
    }

    [Fact]
    public void Groups_AreConsistent()
    {
        Assert.True((StatGroups.FastOwned - StatGroups.PresentMonBacked).IsEmpty);
        Assert.True((StatGroups.Gpu - StatGroups.PresentMonBacked).IsEmpty);
        Assert.True((StatGroups.FrameHistory - StatGroups.PresentMonBacked).IsEmpty);

        // Statistics computed locally must never be sent to PresentMon.
        Assert.False(StatGroups.PresentMonBacked.Contains(StatId.RamUsage));
        Assert.False(StatGroups.PresentMonBacked.Contains(StatId.ProcessRam));
        Assert.False(StatGroups.PresentMonBacked.Contains(StatId.SystemTime));
        Assert.False(StatGroups.PresentMonBacked.Contains(StatId.SessionPlaytime));
    }
}
