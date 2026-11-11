namespace GlassDesk.Platform.Battery;
internal static class MchoseHeadsetProtocol
{
 // Independent implementation of the documented V9 PRO battery GET format.
 // Sources and state evidence: docs/battery-protocols.md. No vendor setters.
 internal static byte[] Request() {
  var request=new byte[64];request[0]=0x55;request[1]=0x65;request[2]=1;return request;
 }
 internal static BatteryReading? Parse(byte[] raw) {
  if(raw.Length!=64||raw[0]!=0x55||raw[1]!=0x65)return null;
  if(raw[3]==2&&raw[2]<=100)return new(raw[2],false);
  if(raw[3] is 0 or 3)return new(null,null,Reason:"已接外部电源；设备百分比可能冻结，暂不显示；无法区分充电中与已充满");
  if(raw[3]==0x1a)return new(null,null,Connected:false,Reason:"耳机已关机或无线连接断开");
  return null;
 }
 internal static BatteryReading? Read(Action<byte[]> write,Func<byte[]> read,CancellationToken token) {
  token.ThrowIfCancellationRequested();write(Request());
  long deadline=Environment.TickCount64+1500;
  for(int i=0;i<8&&Environment.TickCount64<deadline;i++) {
   token.ThrowIfCancellationRequested();
   byte[] raw;
   try {raw=read();}catch(TimeoutException) {return null;}
   token.ThrowIfCancellationRequested();
   if(Parse(raw) is {} battery)return battery;
  }
  return null;
 }
}
