using Clockwork.Overlay;
using Xunit;

namespace Clockwork.Tests;

public class WindowGeometryTests
{
    private static RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static readonly RECT Monitor = Rect(0, 0, 1920, 1080);

    [Fact]
    public void BorderlessGame_ClientAreaCoversTheMonitor()
    {
        Assert.True(WindowGeometry.CoversMonitor(Rect(0, 0, 1920, 1080), Monitor));
    }

    [Fact]
    public void ClientAreaLargerThanTheMonitor_StillCountsAsCovering()
    {
        Assert.True(WindowGeometry.CoversMonitor(Rect(-8, -8, 1928, 1088), Monitor));
    }

    [Fact]
    public void MaximizedAppWithATitleBar_IsNotFullscreen()
    {
        // The window rectangle of a maximized window overshoots the monitor by its invisible
        // border, but its client area starts below the title bar and so never covers the monitor.
        var maximizedClient = Rect(0, 31, 1920, 1080);

        Assert.False(WindowGeometry.CoversMonitor(maximizedClient, Monitor));
    }

    [Fact]
    public void WindowedGame_IsNotFullscreen()
    {
        Assert.False(WindowGeometry.CoversMonitor(Rect(100, 100, 1500, 900), Monitor));
    }

    [Fact]
    public void WorksOnAMonitorWithANonZeroOrigin()
    {
        var secondMonitor = Rect(1920, 0, 3840, 1080);

        Assert.True(WindowGeometry.CoversMonitor(Rect(1920, 0, 3840, 1080), secondMonitor));
        Assert.False(WindowGeometry.CoversMonitor(Rect(0, 0, 1920, 1080), secondMonitor));
    }
}
