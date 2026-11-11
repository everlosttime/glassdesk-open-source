using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;
public class BatteryProtocolTests {
 [Fact] public void ReceiverPresenceDoesNotMeanKc2MouseIsAwake() {
  Assert.True(BatteryProtocols.IsKc2Online([181,3,0,0,0,0,0,1]));
  Assert.False(BatteryProtocols.IsKc2Online([181,3,0,0,0,0,0,0]));
  Assert.False(BatteryProtocols.IsKc2Online([181,6,0,0,0,0,0,1]));
 }
 [Fact] public void MchoseBatteryRequestIsReadCommandAndRejectsStaleCommand() {
  Assert.Equal(new byte[]{0x11,0xF9,0xFF,0xFF},BatteryProtocols.MchoseRequest()[..4]);
  var r=Enumerable.Repeat((byte)0xFF,21).ToArray(); r[0]=0x11;r[1]=0xF9;r[2]=0xAC;r[3]=0xAD;r[4]=0xEF;r[11]=0xB4;r[12]=0xFE;
  Assert.Equal(75,BatteryProtocols.ParseMchose(r)!.Percent);
  r[1]=0xFC;Assert.Null(BatteryProtocols.ParseMchose(r));
 }
 [Fact] public void RazerFrameHasExactBatteryCommandAndCrcAndRejectsInvalidStatus() {
  var q=BatteryProtocols.RazerRequest(0x1F,0x80);
  Assert.Equal(91,q.Length);Assert.Equal(7,q[7]);Assert.Equal(0x80,q[8]);Assert.Equal(0x85,q[89]);
  var r=(byte[])q.Clone();r[1]=2;r[10]=255;r[89]=0x7A;
  Assert.Equal(100,BatteryProtocols.ParseRazer(r,q)!.Percent);
  r[1]=1;Assert.Null(BatteryProtocols.ParseRazer(r,q));
 }
 [Fact] public void MouseEncChecksumAndResponsesMustMatchReadCommand() {
  Assert.Equal(new byte[]{8,4,0},BatteryProtocols.MouseEncRequest(8,4)[..3]);
  Assert.Equal(0x49,BatteryProtocols.MouseEncRequest(8,4)[^1]);
  var r=new byte[17];r[0]=8;r[1]=4;r[6]=100;r[7]=1;
  Assert.Equal(100,BatteryProtocols.ParseMouseEnc(r,8)!.Percent);
  r[1]=3;Assert.Null(BatteryProtocols.ParseMouseEnc(r,8));
 }
 [Fact] public void RapooRejectsUnknownModeAndDoesNotTurnUnknownIntoFullBattery() {
  var r=new byte[]{7,0x20,0,0,0,0,0,2,76};Assert.Equal(76,BatteryProtocols.ParseRapoo(r)!.Percent);
  r[8]=255;Assert.Null(BatteryProtocols.ParseRapoo(r));r[8]=50;r[1]=0;Assert.Null(BatteryProtocols.ParseRapoo(r));
 }
}
