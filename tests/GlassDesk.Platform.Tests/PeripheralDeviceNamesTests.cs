using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;
public sealed class PeripheralDeviceNamesTests
{
    [Fact]
    public void IkfReceiverHasRecognizableNameWithoutInventingBatterySupport()
    {
        var receiver = new HidBatteryDevice("path", "physical", "USB Composite Device", 0x38B1, 0x3412, 0xFF02, 2, 3, 65, 65, 0);
        Assert.Equal("iKF V11 Pro 系列（USB 接收器）", WindowsPeripheralBatteryProvider.DisplayName([receiver]));
        Assert.Equal([PeripheralBatteryCategory.Headset], WindowsPeripheralBatteryProvider.CategoriesForPhysicalDevice([receiver], false));
    }
    [Fact]
    public void GenericDeviceOfAnotherModelIsNotRenamedToIkf()
    {
        var other = new HidBatteryDevice("path", "physical", "Other Device", 0x38B1, 0x9999, 0xFF02, 2, 3, 65, 65, 0);
        Assert.Equal("Other Device", WindowsPeripheralBatteryProvider.DisplayName([other]));
    }
}
