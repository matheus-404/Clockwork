using System.Runtime.InteropServices;
using Clockwork.Services;
using Xunit;

namespace Clockwork.Tests;

public class PresentMonNativeTests
{
    [Fact]
    public void IntrospectionLayouts_MatchThePresentMonCAbi()
    {
        // PM_INTROSPECTION_DEVICE is deliberately only the invariant three-enum prefix:
        // PresentMon 2.3.x and newer releases append different optional fields[cite: 1].
        Assert.Equal(12, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_DEVICE>());

        // PM_INTROSPECTION_METRIC is four enum values plus three pointers on x64[cite: 1].
        // A larger layout shifts the pointers and can cause an access violation while attaching[cite: 1].
        Assert.Equal(40, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_METRIC>());
        Assert.True(PresentMonNative.HasExpectedIntrospectionLayout());
    }

    [Fact]
    public void QueryElement_MatchesPresentMonCAbi()
    {
        // PM_QUERY_ELEMENT must be 32 bytes on x64:
        // 4x 32-bit fields (metric, stat, deviceId, arrayIndex) + 2x 64-bit fields (dataOffset, dataSize).
        Assert.Equal(32, Marshal.SizeOf<PresentMonNative.PM_QUERY_ELEMENT>());
    }

    [Fact]
    public void IntrospectionSubStructures_MatchExpectedSizes()
    {
        // PM_INTROSPECTION_DATA_TYPE_INFO: polledType (4), frameType (4), enumId (4)
        Assert.Equal(12, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_DATA_TYPE_INFO>());

        // PM_INTROSPECTION_STAT_INFO: stat (4)
        Assert.Equal(4, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_STAT_INFO>());

        // PM_INTROSPECTION_DEVICE_METRIC_INFO: deviceId (4), availability (4), arraySize (4)
        Assert.Equal(12, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_DEVICE_METRIC_INFO>());

        // PM_INTROSPECTION_OBJARRAY: pData (8), size (8) on x64
        Assert.Equal(16, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_OBJARRAY>());

        // PM_INTROSPECTION_ROOT: 4 pointers on x64 (pMetrics, pEnums, pDevices, pUnits)
        Assert.Equal(32, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_ROOT>());
    }
}