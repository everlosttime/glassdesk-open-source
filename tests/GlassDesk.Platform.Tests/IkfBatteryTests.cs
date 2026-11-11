using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;

public class IkfBatteryTests
{
 private static byte[] Battery(byte percent=50,byte sequence=7)=>[3,0x55,0x80,0,8,0,0xff,sequence,4,0x0c,percent,0,0,0xaa];
 private static byte[] Online()=>[3,9,0xd6,0xbe,0,2,0,0xc7,0x60,10,1];
 [Fact] public void OnlyTheVerifiedReceiverAndVendorCollectionAreQueried() {
  var d=new HidBatteryDevice("path","physical","iKF-V11 Pro 2.0",0x38b1,0x3419,0xff00,1,4,64,64,0);
  Assert.Equal("Ikf",HidBatteryModels.Find(d)?.Protocol);
  Assert.Null(HidBatteryModels.Find(d with {ProductId=0x3412}));
  Assert.Null(HidBatteryModels.Find(d with {UsagePage=0xff02}));
  Assert.Null(HidBatteryModels.Find(d with {InterfaceNumber=3}));
  Assert.Null(HidBatteryModels.Find(d with {InputLength=65}));
 }
 [Fact] public void GetterFramesMatchCapturedProtocolWithoutChangingSettings() {
  Assert.Equal(new byte[]{2,7,0x5a,0xaa,0xa5,0x81,0x97,1,10},IkfBatteryProtocol.ConnectionRequest()[..9]);
  Assert.Equal(new byte[]{2,0x55,0x80,0,6,0,0xff,7,2,0xfa,12,0xaa},IkfBatteryProtocol.BatteryRequest(7)[..12]);
  Assert.Equal(64,IkfBatteryProtocol.BatteryRequest(7).Length);
 }
 [Theory] [InlineData(0)] [InlineData(50)] [InlineData(100)]
 public void CapturedSingleHeadsetBatteryUsesItsOwnChannel(int percent) {
  var reading=IkfBatteryProtocol.ParseBattery(Battery((byte)percent),7);
  Assert.NotNull(reading);Assert.Equal(percent,reading.Percent);Assert.Null(reading.Charging);
 }
 [Fact] public void AckOtherSequencesUnknownPercentAndMalformedFramesAreRejected() {
  Assert.Null(IkfBatteryProtocol.ParseBattery([3,0x55,0x80,0,5,0,0xff,7,1,0xfe,0xaa],7));
  Assert.Null(IkfBatteryProtocol.ParseBattery(Battery(sequence:8),7));
  Assert.Null(IkfBatteryProtocol.ParseBattery(Battery(255),7));
  var r=Battery();r[^1]=0;Assert.Null(IkfBatteryProtocol.ParseBattery(r,7));
  r=Battery();r[2]=0;Assert.Null(IkfBatteryProtocol.ParseBattery(r,7));
  r=Battery();r[4]=9;Assert.Null(IkfBatteryProtocol.ParseBattery(r,7));
  r=Battery();r[8]=3;Assert.Null(IkfBatteryProtocol.ParseBattery(r,7));
  Assert.Null(IkfBatteryProtocol.ParseBattery(Battery()[..10],7));
 }
 [Fact] public void ConnectionRequiresTheCorrectCommandCrcAndWirelessStatus() {
  Assert.True(IkfBatteryProtocol.ParseConnection(Online()));
  var r=Online();r[7]^=1;Assert.Null(IkfBatteryProtocol.ParseConnection(r));
  Assert.False(IkfBatteryProtocol.ParseConnection([3,9,0xd6,0xbe,0,2,0,6,0xa0,10,0]));
  Assert.Null(IkfBatteryProtocol.ParseConnection([3,9,0xd6,0xbe,0,2,0,6,0xa0,10]));
 }
 [Fact] public void QuerySkipsAcknowledgementsAndUnrelatedResponses() {
  var responses=new Queue<byte[]>([Online(),[3,0x55,0x80,0,5,0,0xff,7,1,0xfe,0xaa],Battery(sequence:0),Battery()]);
  var writes=new List<byte[]>();
  Assert.Equal(50,IkfBatteryProtocol.Read(writes.Add,responses.Dequeue,7,CancellationToken.None)?.Percent);
  Assert.Equal(2,writes.Count);Assert.Empty(responses);
 }
 [Fact] public void DisconnectedAndCanceledQueriesNeverPublishBatteryOrSendFurtherCommands() {
  var writes=new List<byte[]>();
  Assert.Null(IkfBatteryProtocol.Read(writes.Add,()=>[3,9,0xd6,0xbe,0,2,0,6,0xa0,10,0],7,CancellationToken.None));
  Assert.Single(writes);
  using var canceled=new CancellationTokenSource();canceled.Cancel();
  Assert.Throws<OperationCanceledException>(()=>IkfBatteryProtocol.Read(writes.Add,Online,7,canceled.Token));
  Assert.Single(writes);
 }
 [Fact] public void MissingRepliesAreBoundedAndReleaseTheCaller() {
  int attempts=0;
  Assert.Null(IkfBatteryProtocol.Read(_=>{},()=>{attempts++;throw new TimeoutException();},7,CancellationToken.None));
  Assert.InRange(attempts,1,3);
 }
}
