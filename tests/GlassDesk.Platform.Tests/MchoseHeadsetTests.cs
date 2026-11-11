using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;

public class MchoseHeadsetTests
{
 private static byte[] Reply(byte percent=90,byte state=2) {var r=new byte[64];r[0]=0x55;r[1]=0x65;r[2]=percent;r[3]=state;return r;}
 [Fact] public void V9ProRequiresItsExactBatteryCollectionAndReportLengths() {
  var d=new HidBatteryDevice("path","physical","MCHOSE V9 PRO",0x291d,0x385d,0xff90,1,0,64,64,0);
  Assert.Equal("MchoseHeadset",HidBatteryModels.Find(d)?.Protocol);
  Assert.Null(HidBatteryModels.Find(d with {UsagePage=0xff82}));
  Assert.Null(HidBatteryModels.Find(d with {UsagePage=0xff22}));
  Assert.Null(HidBatteryModels.Find(d with {Usage=2}));
  Assert.Null(HidBatteryModels.Find(d with {InputLength=65}));
  Assert.Null(HidBatteryModels.Find(d with {OutputLength=0}));
  Assert.Null(HidBatteryModels.Find(d with {ProductId=0x385e}));
  Assert.Null(HidBatteryModels.Find(d with {VendorId=0x3837,ProductId=0x6008}));
 }
 [Fact] public void RequestIsOnlyTheKnown64ByteBatteryGetter() {
  var expected=new byte[64];expected[0]=0x55;expected[1]=0x65;expected[2]=1;
  Assert.Equal(expected,MchoseHeadsetProtocol.Request());
 }
 [Theory] [InlineData(0)] [InlineData(90)] [InlineData(100)]
 public void BatteryStatePreservesTheDeviceReportedPercentage(int percent) {
  var r=MchoseHeadsetProtocol.Parse(Reply((byte)percent));
  Assert.NotNull(r);Assert.Equal(percent,r.Percent);Assert.False(r.Charging);
 }
 [Theory] [InlineData(0)] [InlineData(3)]
 public void ExternalPowerHidesTheFrozenPercentageWithoutClaimingChargeOrFull(int state) {
  var r=MchoseHeadsetProtocol.Parse(Reply(90,(byte)state));
  Assert.NotNull(r);Assert.Null(r.Percent);Assert.Null(r.Charging);
  Assert.Contains("外部电源",r.Reason);
 }
 [Fact] public void OffAndMalformedReportsNeverBecomeZeroOrFullBattery() {
  var off=MchoseHeadsetProtocol.Parse(Reply(0,0x1a));
  Assert.NotNull(off);Assert.False(off.Connected);Assert.Null(off.Percent);
  Assert.Null(MchoseHeadsetProtocol.Parse(Reply(255)));
  Assert.Null(MchoseHeadsetProtocol.Parse(Reply(50,1)));
  Assert.Null(MchoseHeadsetProtocol.Parse(Reply()[..63]));
  var unrelated=Reply();unrelated[1]=0x11;Assert.Null(MchoseHeadsetProtocol.Parse(unrelated));
  unrelated=Reply();unrelated[0]=0;Assert.Null(MchoseHeadsetProtocol.Parse(unrelated));
 }
 [Fact] public void QuerySkipsUnrelatedReportsAndSendsOnlyOneRequest() {
  var other=Reply();other[1]=0x11;
  var replies=new Queue<byte[]>([other,Reply()]);var writes=new List<byte[]>();
  Assert.Equal(90,MchoseHeadsetProtocol.Read(writes.Add,replies.Dequeue,CancellationToken.None)?.Percent);
  Assert.Single(writes);Assert.Equal(MchoseHeadsetProtocol.Request(),writes[0]);
 }
 [Fact] public void MissingRepliesAndCancellationAreBoundedWithoutMoreWrites() {
  int reads=0,writes=0;
  Assert.Null(MchoseHeadsetProtocol.Read(_=>writes++,()=>{reads++;throw new TimeoutException();},CancellationToken.None));
  Assert.Equal(1,reads);Assert.Equal(1,writes);
  using var cancel=new CancellationTokenSource();cancel.Cancel();
  Assert.Throws<OperationCanceledException>(()=>MchoseHeadsetProtocol.Read(_=>writes++,()=>Reply(),cancel.Token));
  Assert.Equal(1,writes);
 }
}
