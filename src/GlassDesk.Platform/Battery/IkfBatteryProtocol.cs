namespace GlassDesk.Platform.Battery;
internal static class IkfBatteryProtocol
{
 // Wire format verified against this machine's converted V11 Pro 2.0.
 // Only receiver connection and headset battery GET commands are sent.
 internal static byte[] ConnectionRequest() {
  var request=new byte[64];
  new byte[]{2,7,0x5a,0xaa,0xa5,0x81,0x97,1,10}.CopyTo(request,0);
  return request;
 }
 internal static byte[] BatteryRequest(byte sequence) {
  var request=new byte[64];
  new byte[]{2,0x55,0x80,0,6,0,0xff,sequence,2,0xfa,12,0xaa}.CopyTo(request,0);
  return request;
 }
 internal static bool? ParseConnection(byte[] raw) {
  var r=raw.AsSpan();
  if(r.Length<11||r[0]!=3)return null;
  r=r[1..];
  if(r[0]!=9||r[1]!=0xd6||r[2]!=0xbe||r[3]!=0||r[4]!=2||r[5]!=0||r[8]!=10)return null;
  if(Crc(r.Slice(8,2))!=(r[6]|r[7]<<8))return null;
  return r[9]==1; // A receiver alone, or wired mode, is not an online wireless headset.
 }
 internal static BatteryReading? ParseBattery(byte[] raw,byte sequence) {
  var r=raw.AsSpan();
  if(r.Length<14||r[0]!=3||r[1]!=0x55||r[2]!=0x80||r[3]!=0)return null;
  int length=r[4]|r[5]<<8;
  if(length<8||length>58||r.Length<6+length)return null;
  var app=r.Slice(6,length);
  if(app[0]!=0xff||app[1]!=sequence||app[2]+4!=app.Length||app[^1]!=0xaa)return null;
  var payload=app.Slice(3,app[2]);
  // This single-battery headset reports its percentage in the left channel.
  // Acknowledgements (FE), firmware and other properties cannot supply it.
  if(payload[0]==0xfa)payload=payload[1..];
  if(payload.Length<4||payload[0]!=12||payload[1]>100)return null;
  return new(payload[1],null); // Charging semantics have not been verified on this firmware.
 }
 internal static BatteryReading? Read(Action<byte[]> write,Func<byte[]> read,byte sequence,CancellationToken token) {
  token.ThrowIfCancellationRequested();write(ConnectionRequest());
  bool online=false;
  for(int i=0;i<3;i++) {
   token.ThrowIfCancellationRequested();
   try {if(ParseConnection(read()) is {} connected) {online=connected;break;}}
   catch(TimeoutException) { }
  }
  if(!online)return null;
  token.ThrowIfCancellationRequested();write(BatteryRequest(sequence));
  for(int i=0;i<6;i++) {
   token.ThrowIfCancellationRequested();
   try {if(ParseBattery(read(),sequence) is {} battery)return battery;}
   catch(TimeoutException) { }
  }
  return null;
 }
 private static ushort Crc(ReadOnlySpan<byte> data) {
  ushort value=0;
  foreach(byte b in data) {value^=b;for(int i=0;i<8;i++)value=(ushort)((value>>1)^((value&1)!=0?0xa001:0));}
  return value;
 }
}
