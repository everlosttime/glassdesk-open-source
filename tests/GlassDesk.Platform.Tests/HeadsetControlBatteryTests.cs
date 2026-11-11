using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;
public sealed class HeadsetControlBatteryTests
{
    [Theory]
    [InlineData("BATTERY_AVAILABLE", 0, 0)]
    [InlineData("BATTERY_CHARGING", 100, 100)]
    [InlineData("BATTERY_UNAVAILABLE", 80, null)]
    [InlineData("BATTERY_TIMEOUT", 80, null)]
    [InlineData("BATTERY_AVAILABLE", 101, null)]
    public void OnlyCurrentBoundedReadingsAreShown(string status, int level, int? expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { devices = new[] { new { device = "Test", id_vendor = "0x046d", id_product = "0x0a38", battery = new { status, level } } } });
        var result = HeadsetControlBatteryProvider.Parse(json, [Device("one")], DateTimeOffset.UtcNow);
        Assert.Equal(expected, Assert.Single(result).Percent);
    }
    [Fact]
    public void SameModelMultiplePhysicalDevicesNeverShareAnUnidentifiedReading()
    {
        const string json = """{"devices":[{"device":"Test","id_vendor":"0x046d","id_product":"0x0a38","battery":{"status":"BATTERY_AVAILABLE","level":55}}]}""";
        var result = HeadsetControlBatteryProvider.Parse(json, [Device("one"), Device("two")], DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Null(item.Percent));
        Assert.Equal(2, result.Select(item => item.DeviceKey).Distinct().Count());
    }
    [Fact]
    public void MissingPhysicalIdentityDoesNotCreateANameBasedDevice()
    {
        Assert.Empty(HeadsetControlBatteryProvider.Parse("""{"devices":[{"device":"Test","id_vendor":"0x046d","id_product":"0x0a38"}]}""", [], DateTimeOffset.UtcNow));
    }
    private static HidBatteryDevice Device(string id) => new(id,id,"Test",0x046d,0x0a38,0xff00,1,1,64,64,64);
}
