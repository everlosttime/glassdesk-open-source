namespace GlassDesk.Platform.Battery;
internal sealed record BatteryReading(int? Percent,bool? Charging,string? Level=null,bool Connected=true,string? Reason=null);
internal static class BatteryProtocols
{
 internal static BatteryReading? ParseAsus(byte[] raw,HidBatteryModel model) {
  var r=raw.AsSpan();if(r.Length>2&&r[0]==model.Report&&r[1]==0x12)r=r[1..];
  if(r.Length<=Math.Max(model.Offset,model.ChargeOffset)||r[0]!=0x12||r[1]!=model.Arg)return null;
  int percent=r[model.Offset]*model.Multiplier;if(percent>100)return null;
  return model.Multiplier>1?new(null,r[model.ChargeOffset]!=0,$"~{percent}%",Reason:"设备仅提供电量档位，此值为近似电量")
   :new(percent,r[model.ChargeOffset]!=0);
 }
 internal static bool IsKc2Online(byte[] raw) {
  var r=raw.AsSpan();if(r.Length>0&&r[0]==181)r=r[1..];
  return r.Length>=7&&r[0]==3&&r[6]==1;
 }
 internal static byte[] MchoseRequest() { var q=Enumerable.Repeat((byte)0xFF,21).ToArray();q[0]=0x11;q[1]=0xF9;return q; }
 internal static BatteryReading? ParseMchose(byte[] raw) {
  if(raw.Length<13||raw[0]!=0x11||raw[1]!=0xF9) return null;
  var r=raw.Skip(1).Select(b=>(byte)(b^255)).ToArray();
  if((r[1]|r[2])==0 || (r[3]|r[4])==0 || r[10]>100 || r[11]>1) return null;
  return new(r[10],r[11]==1);
 }
 internal static byte[] RazerRequest(byte transaction,byte command) {
  var q=new byte[91];q[2]=transaction;q[6]=2;q[7]=7;q[8]=command;
  for(int i=3;i<=88;i++) q[89]^=q[i]; return q;
 }
 internal static BatteryReading? ParseRazer(byte[] raw,byte[] request) {
  if(raw.Length<91||raw[1]!=2||raw[2]!=request[2]||raw[7]!=7||raw[8]!=request[8]) return null;
  byte crc=0;for(int i=3;i<=88;i++) crc^=raw[i];
  // Some firmware leaves response CRC zero. Commands, transaction and successful status remain mandatory.
  if(raw[89]!=0&&raw[89]!=crc) return null;
  return new((raw[10]*100+127)/255,null);
 }
 internal static byte[] MouseEncRequest(byte report,byte command) { var q=new byte[17];q[0]=report;q[1]=command;q[^1]=unchecked((byte)(0x55-command-report));return q; }
 internal static BatteryReading? ParseMouseEnc(byte[] r,byte report) => r.Length>=10&&r[0]==report&&r[1]==4&&r[2]==0&&r[6]<=100?new(r[6],r[7]==1):null;
 internal static BatteryReading? ParseRapoo(byte[] r)=>r.Length>=9&&r[0]==7&&(r[1]&0xF0) is 0x20 or 0x10&&r[8] is >=2 and <=100?new(r[8],(r[7]&2)!=0||(r.Length>9&&r[9] is 1 or 2)):null;
 internal static byte[] Payload(byte report,int length,params byte[] data) { var q=new byte[Math.Max(length,data.Length+1)];q[0]=report;data.CopyTo(q,1);return q; }
 internal static BatteryReading? ParseVgn(string protocol,byte[] raw,byte report) {
  var r=raw.AsSpan();if(r.Length>0&&r[0]==report)r=r[1..];
  int p=-1;bool? c=null;
  switch(protocol) {
   case "ThreeMode" when r.Length>=5&&r[0]==13&&r[1]==1:p=r[4];c=r[3]!=0;break;
   case "Weisheng" when r.Length>=9&&r[2]==26:p=r[7];c=r[8]!=0;break;
   case "Beiying" when r.Length>=6&&r[0]==74:p=r[4];c=(r[5]>>4)==1;break;
   case "VgnKc2" when r.Length>=20&&r[0]==6:p=r[19]&127;c=(r[19]&128)!=0;break;
   case "VgnLdMs" when r.Length>=3&&r[0]==2&&r[1]==37:p=r[2];break;
  }
  return p is >=0 and <=100?new(p,c):null;
 }
}
