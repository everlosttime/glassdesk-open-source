using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;
public class BatteryEvidenceParityTests
{
 [Fact] public void CoarseAsusRegistersNeverBecomeAnExactPercentage() {
  var model=HidBatteryModels.All.First(m=>m.Vid==0x0b05&&m.Pid==0x1960);
  var raw=new byte[10];raw[0]=0x12;raw[1]=7;raw[4]=2;raw[9]=1;
  var reading=BatteryProtocols.ParseAsus(raw,model);
  Assert.NotNull(reading);Assert.Null(reading.Percent);Assert.Equal("~50%",reading.Level);Assert.True(reading.Charging);
  raw[4]=5;Assert.Null(BatteryProtocols.ParseAsus(raw,model));
 }
 [Fact] public void FullResolutionAsusModelKeepsItsReportedPercentage() {
  var model=HidBatteryModels.All.First(m=>m.Vid==0x0b05&&m.Pid==0x1a1a);
  var raw=new byte[10];raw[0]=0x12;raw[1]=7;raw[4]=73;
  var reading=BatteryProtocols.ParseAsus(raw,model);
  Assert.NotNull(reading);Assert.Equal(73,reading.Percent);Assert.Null(reading.Level);
 }
 [Fact] public void WindowsBleBatteryPropertiesRemainAvailableWithoutStandardGatt() {
  var properties=new Dictionary<string,object>();
  (string Id,string? Container,int? Percent)[] nodes=[("USB\\receiver","same",99),("BTHLE\\other","other",10),("BTHLE\\battery","same",64)];
  Assert.Equal(64,WindowsPeripheralBatteryProvider.BluetoothPropertyPercent(properties,"same",nodes));
  Assert.Null(WindowsPeripheralBatteryProvider.BluetoothPropertyPercent(properties,"missing",nodes));
  properties["System.Devices.BatteryLife"]=75;
  Assert.Equal(75,WindowsPeripheralBatteryProvider.BluetoothPropertyPercent(properties,"same",nodes));
 }
}
