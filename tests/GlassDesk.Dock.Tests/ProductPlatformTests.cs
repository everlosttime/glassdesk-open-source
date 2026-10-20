using System.Reflection;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class ProductPlatformTests
{
    [Theory]
    [InlineData(19045, false)]
    [InlineData(21999, false)]
    [InlineData(22000, true)]
    [InlineData(26300, true)]
    [InlineData(40000, true)]
    public void ProductFloorHasNoUpperBuildLimit(int build, bool supported)
    {
        var method = typeof(WindowsCapabilities).GetMethod("IsProductBuildSupported", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(supported, method.Invoke(null, [build]));
    }
}
