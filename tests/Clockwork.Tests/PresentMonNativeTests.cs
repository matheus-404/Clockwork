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
        // PresentMon 2.3.x and newer releases append different optional fields.
        Assert.Equal(12, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_DEVICE>());

        // PM_INTROSPECTION_METRIC is four enum values plus three pointers on x64.
        // A larger layout shifts the pointers and can cause an access violation while attaching.
        Assert.Equal(40, Marshal.SizeOf<PresentMonNative.PM_INTROSPECTION_METRIC>());
        Assert.True(PresentMonNative.HasExpectedIntrospectionLayout());
    }
}
