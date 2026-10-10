using System.Globalization;
using Clockwork.Overlay;
using Xunit;

namespace Clockwork.Tests;

public class OverlayFormattingTests
{
    [Fact]
    public void FormatNumber_IgnoresTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");

            Assert.Equal("12.3 ms", OverlayStatFormatter.FormatNumber(12.3, 1, " ms"));
            Assert.Equal("60", OverlayStatFormatter.FormatNumber(60, 0, ""));
            Assert.Equal("8.0 / 15.9 GB", OverlayStatFormatter.FormatRam(8.04, 15.9));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(59, "00:00:59")]
    [InlineData(3661, "01:01:01")]
    [InlineData(360000, "100:00:00")]
    [InlineData(-5, "00:00:00")]
    public void FormatPlaytime_ShowsHoursMinutesSeconds(long seconds, string expected)
    {
        Assert.Equal(expected, OverlayStatFormatter.FormatPlaytime(seconds));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(23 * 60 + 5, "23:05")]
    [InlineData(9 * 60 + 30, "09:30")]
    public void FormatClock_ShowsHoursAndMinutes(long minutes, string expected)
    {
        Assert.Equal(expected, OverlayStatFormatter.FormatClock(minutes));
    }

    [Fact]
    public void FormatFlag_ShowsYesOrNo()
    {
        Assert.Equal("Yes", OverlayStatFormatter.FormatFlag(1));
        Assert.Equal("No", OverlayStatFormatter.FormatFlag(0));
    }

    [Fact]
    public void NumberSlot_OnlyReportsChangesThatAreVisible()
    {
        var slot = new StatSlot(StatRegistry.Get(StatId.FrameTime)); // one decimal, " ms"

        Assert.True(slot.TryUpdateNumber(16.66, null, out var first));
        Assert.Equal("16.7 ms", first);

        // 16.70 rounds to the same 16.7, so nothing is formatted or posted.
        Assert.False(slot.TryUpdateNumber(16.70, null, out _));

        Assert.True(slot.TryUpdateNumber(16.9, null, out var changed));
        Assert.Equal("16.9 ms", changed);
    }

    [Fact]
    public void NumberSlot_FallsBackToStatusTextAndBack()
    {
        var slot = new StatSlot(StatRegistry.Get(StatId.GpuRenderCompute));

        Assert.True(slot.TryUpdateNumber(null, "Unsupported", out var status));
        Assert.Equal("Unsupported", status);
        Assert.False(slot.TryUpdateNumber(null, "Unsupported", out _));

        Assert.True(slot.TryUpdateNumber(double.NaN, null, out var notAvailable));
        Assert.Equal("N/A", notAvailable);

        Assert.True(slot.TryUpdateNumber(42, null, out var number));
        Assert.Equal("42%", number);

        // Going back to the same number after a status is a change that must be shown.
        Assert.True(slot.TryUpdateNumber(null, "Unavailable", out _));
        Assert.True(slot.TryUpdateNumber(42, null, out var again));
        Assert.Equal("42%", again);
    }

    [Fact]
    public void NumberSlot_NeverPrintsNegativeZero()
    {
        var slot = new StatSlot(StatRegistry.Get(StatId.Fps));

        Assert.True(slot.TryUpdateNumber(-0.2, null, out var text));
        Assert.Equal("0", text);
    }

    [Fact]
    public void FlagSlot_ShowsYesNoAndStatus()
    {
        var slot = new StatSlot(StatRegistry.Get(StatId.GpuPowerLimited));

        Assert.True(slot.TryUpdateFlag(1, null, out var yes));
        Assert.Equal("Yes", yes);
        Assert.False(slot.TryUpdateFlag(1, null, out _));

        Assert.True(slot.TryUpdateFlag(0, null, out var no));
        Assert.Equal("No", no);

        Assert.True(slot.TryUpdateFlag(null, "Unsupported", out var status));
        Assert.Equal("Unsupported", status);
    }

    [Fact]
    public void KeyedSlot_FormatsOnlyWhenTheKeyChanges()
    {
        var slot = new StatSlot(StatRegistry.Get(StatId.SessionPlaytime));
        var calls = 0;

        string Format(long seconds)
        {
            calls++;
            return OverlayStatFormatter.FormatPlaytime(seconds);
        }

        Assert.True(slot.TryUpdateKeyed(61, Format, out var text));
        Assert.Equal("00:01:01", text);
        Assert.False(slot.TryUpdateKeyed(61, Format, out _));
        Assert.True(slot.TryUpdateKeyed(62, Format, out _));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TextSlot_ComparesTheText()
    {
        var slot = new StatSlot(StatRegistry.Get(StatId.RamUsage));

        Assert.True(slot.TryUpdateText("8.0 / 16.0 GB", out _));
        Assert.False(slot.TryUpdateText("8.0 / 16.0 GB", out _));
        Assert.True(slot.TryUpdateText("8.1 / 16.0 GB", out _));
    }
}
