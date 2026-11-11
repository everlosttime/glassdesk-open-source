using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;
public class BatteryTests
{
 [Fact] public void ReceiverPlaceholderDoesNotHideSlotBatteryAndDefaultAudioIsPreserved() {
  var placeholder=new PeripheralBatterySnapshot("hid:a:mouse","receiver",PeripheralBatteryCategory.Mouse,true,null,null,DateTimeOffset.UtcNow,"Windows HID","unknown","",PhysicalDeviceId:"a");
  var slot=placeholder with {DeviceKey="hid:a:mouse:slot1",Percent=75,ReceiverSlot=1,Source="HID Logitech"};
  var merged=PeripheralBatteryService.Merge([placeholder,slot]);
  Assert.Single(merged);Assert.Equal(75,PeripheralBatteryService.Select(merged,PeripheralBatteryCategory.Mouse,null)!.Percent);
 }
 [Fact] public void ExpiredReadingsAreNotPresentedAsLivePercentages() {
  var old=new PeripheralBatterySnapshot("hid:a:mouse","mouse",PeripheralBatteryCategory.Mouse,true,75,null,DateTimeOffset.UtcNow.AddHours(-1),"HID","supported","");
  Assert.Null(PeripheralBatteryService.Select([old],PeripheralBatteryCategory.Mouse,null)!.Percent);
 }
 [Fact] public void PhysicalDeviceKeysDoNotJoinNamesOrReceiverSlots() {
  var a = new PeripheralBatterySnapshot("usb:a:slot1", "same", PeripheralBatteryCategory.Mouse, true, 91, false, DateTimeOffset.UtcNow, "HID", "supported", "", 1);
  var b = a with {DeviceKey="usb:a:slot2", Percent=12, ReceiverSlot=2};
  var bt = a with {DeviceKey="bt:a", Percent=5};
  Assert.Equal(3, PeripheralBatteryService.Merge([a,b,bt]).Count);
  Assert.Equal(12, PeripheralBatteryService.Select([a,b,bt], PeripheralBatteryCategory.Mouse, b.DeviceKey)!.Percent);
 }
 [Fact] public void ManualMissingDeviceDoesNotFallBackToAnotherDevice() {
  Assert.Null(PeripheralBatteryService.Select([new("usb:a","A",PeripheralBatteryCategory.Mouse,true,50,null,DateTimeOffset.UtcNow,"HID","supported","")],PeripheralBatteryCategory.Mouse,"bt:missing"));
 }
 [Fact] public void AutomaticChoiceRetainsConnectedDeviceAndIgnoresOffline() {
  PeripheralBatterySnapshot D(string key,bool connected)=>new(key,key,PeripheralBatteryCategory.Mouse,connected,50,null,DateTimeOffset.UtcNow,"HID","supported","");
  Assert.Equal("b", PeripheralBatteryService.Select([D("a",true),D("b",true)],PeripheralBatteryCategory.Mouse,null,"b")!.DeviceKey);
  Assert.Equal("a", PeripheralBatteryService.Select([D("a",true),D("b",false)],PeripheralBatteryCategory.Mouse,null,"b")!.DeviceKey);
 }
}
