namespace GlassDesk.Platform.Battery;
internal sealed class NativeHidBatteryProvider : IPeripheralBatteryProvider
{
 private readonly Dictionary<string,string> _successfulPaths=new(StringComparer.OrdinalIgnoreCase);
 private static int _ikfSequence=127;
 internal IReadOnlyCollection<PeripheralBatteryCategory>? Categories {get;set;}
 public Task<IReadOnlyList<PeripheralBatterySnapshot>> QueryAsync(CancellationToken cancellationToken)=>Task.Run<IReadOnlyList<PeripheralBatterySnapshot>>(()=>Query(cancellationToken),cancellationToken);
 private List<PeripheralBatterySnapshot> Query(CancellationToken token) {
  var devices=NativeHidTransport.Enumerate();var results=new List<PeripheralBatterySnapshot>();
  var candidates=devices.Select(d=>(Device:d,Model:HidBatteryModels.Find(d)))
   .Where(x=>x.Model is not null && (Categories is null||Categories.Contains(x.Model.Category)||(x.Model.Protocol=="Logitech"&&Categories.Contains(PeripheralBatteryCategory.Keyboard))) && x.Device.UsagePage>0x0B)
   .GroupBy(x=>$"{x.Device.PhysicalId}:{x.Model!.Category}:{x.Model.Protocol}");
  foreach(var group in candidates) {
   token.ThrowIfCancellationRequested();var first=group.First();var model=first.Model!;
   if(model.Protocol=="Logitech") { results.AddRange(QueryLogitech(group.Select(x=>x.Device).ToArray(),model,token));continue; }
   var key=$"hid:{first.Device.PhysicalId}:{model.Category.ToString().ToLowerInvariant()}";
   PeripheralBatterySnapshot? reading=null;string? failure=null;
   var support=model.Protocol switch {"Ikf"=>"GlassDesk 本机实测：V11 Pro 2.0 新自研固件；其他固件未验证","MchoseHeadset"=>"GlassDesk 本机实测：MCHOSE V9 PRO 放电读数；充电与关机状态依据上游协议，待本机验证",_=>"上游协议适配；GlassDesk 未逐型号实测"};
   var choices=_successfulPaths.TryGetValue(key,out var cached)&&group.Any(x=>x.Device.Path==cached)?group.Where(x=>x.Device.Path==cached):group;
   if(model.Protocol=="MchoseHeadset"&&group.Count()!=1) {choices=[];failure="电量通信接口不唯一，未发送查询";}
   foreach(var candidate in choices) {
    token.ThrowIfCancellationRequested();
    try {
     var d=candidate.Device;using var transport=new NativeHidTransport(d.Path);
     var result=Read(transport,d,model,token);
     if(result is null) continue;
     reading=new(key,model.Name,model.Category,result.Connected,result.Percent,result.Charging,DateTimeOffset.UtcNow,"HID "+model.Protocol,support,result.Reason??"",PhysicalDeviceId:d.PhysicalId,Level:result.Level);
     _successfulPaths[key]=d.Path;break;
    } catch(OperationCanceledException) { throw; }
    catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or TimeoutException or IOException or UnauthorizedAccessException) { failure=ex.GetType().Name+": "+ex.Message; }
   }
   results.Add(reading??new(key,model.Name,model.Category,false,null,null,DateTimeOffset.UtcNow,"HID "+model.Protocol,support,"设备休眠、未连接、被占用或未返回有效电量"+(failure is null?"":"；"+failure),PhysicalDeviceId:first.Device.PhysicalId));
  }
  return results;
 }
 private static BatteryReading? Read(NativeHidTransport t,HidBatteryDevice d,HidBatteryModel m,CancellationToken token) {
  switch(m.Protocol) {
   case "MchoseHeadset":
    return MchoseHeadsetProtocol.Read(q=>t.Write(q,token),()=>t.Read(d.InputLength,token),token);
   case "Ikf":
    byte sequence=(byte)(128+(Interlocked.Increment(ref _ikfSequence)&127));
    return IkfBatteryProtocol.Read(q=>t.Write(q,token),()=>t.Read(d.InputLength,token),sequence,token);
   case "Mchose":
    for(int i=0;i<4;i++) {
     var response=t.Feature(BatteryProtocols.MchoseRequest(),21,token,40);
     if(BatteryProtocols.ParseMchose(response) is {} battery)return battery;
     if(token.WaitHandle.WaitOne(40))token.ThrowIfCancellationRequested();
    }
    return null;
   case "Razer":
    var q=BatteryProtocols.RazerRequest((byte)m.Arg,0x80);
    for(int i=0;i<2;i++) {
     var raw=t.Feature(q,91,token,m.Wait);var battery=BatteryProtocols.ParseRazer(raw,q);
     if(battery is null)continue;
     try { var charge=t.Feature(BatteryProtocols.RazerRequest((byte)m.Arg,0x84),91,token,m.Wait);if(charge.Length>=11&&charge[1]==2&&charge[7]==7&&charge[8]==0x84)battery=battery with{Charging=charge[10]!=0}; } catch(TimeoutException) { }
     return battery;
    }
    return null;
   case "Asus":
    t.Write(BatteryProtocols.Payload((byte)m.Report,(d.OutputLength>0?d.OutputLength:65),0x12,(byte)m.Arg),token);
    for(int i=0;i<3;i++) {
     var raw=t.Read(d.InputLength>0?d.InputLength:65,token);
     if(BatteryProtocols.ParseAsus(raw,m) is {} battery)return battery;
    }
    return null;
   case "Aula":return ReadAula(t,d,token);
   case "Rapoo":return BatteryProtocols.ParseRapoo(t.Read(Math.Max(19,d.InputLength),token));
   case "MouseEnc":
    t.Write(Pad(BatteryProtocols.MouseEncRequest((byte)m.Report,3),d.OutputLength),token);
    var online=t.Read(Math.Max(17,d.InputLength),token);
    if(online.Length<7||online[0]!=m.Report||online[1]!=3||online[2]!=0||online[6]==0)return null;
    t.Write(Pad(BatteryProtocols.MouseEncRequest((byte)m.Report,4),d.OutputLength),token);
    return BatteryProtocols.ParseMouseEnc(t.Read(Math.Max(17,d.InputLength),token),(byte)m.Report);
   default:
    if(m.Protocol=="VgnKc2"&&d.ProductId==0x1A23) {
     t.Write(Pad(BatteryProtocols.Payload(181,64,3),d.OutputLength),token);
     var status=t.Read(Math.Max(64,d.InputLength),token);
     if(!BatteryProtocols.IsKc2Online(status))return null;
    }
    byte[] command=m.Protocol switch {
     "ThreeMode"=>BatteryProtocols.Payload((byte)m.Report,65,13,1),
     "Weisheng"=>BatteryProtocols.Payload((byte)m.Report,8,0,0,26,6,0,0,0),
     "Beiying"=>BatteryProtocols.Payload((byte)m.Report,21,74,1),
     "VgnKc2"=>BatteryProtocols.Payload((byte)m.Report,21,6),
     "VgnLdMs"=>BatteryProtocols.Payload((byte)m.Report,34,2,37),
     _=>[]};
    if(command.Length==0)return null;
    if(m.Protocol=="Beiying")command[^1]=unchecked((byte)(m.Report+75));
    if(m.Protocol=="VgnLdMs")command[^1]=216;
    t.Write(Pad(command,d.OutputLength),token);
    return BatteryProtocols.ParseVgn(m.Protocol,t.Read(Math.Max(d.InputLength,65),token),(byte)m.Report);
  }
 }
 private static byte[] Pad(byte[] data,int size) {if(data.Length>=size)return data;Array.Resize(ref data,size);return data;}
 private static BatteryReading? ReadAula(NativeHidTransport t,HidBatteryDevice d,CancellationToken token) {
  if(d.OutputLength<28)return null;
  foreach(var (cmd,total,last) in new[]{(0x10,56,true),(0x10,48,true),(0xA0,64,false)}) {
   // Battery fields lie in first 19 bytes of GET_DEVICE_INFO; later chunks are unnecessary.
   var q=new byte[d.OutputLength];q[1]=0xAA;q[2]=(byte)cmd;q[3]=(byte)Math.Min(total,d.OutputLength-9);q[7]=(byte)(last&&total<=d.OutputLength-9?1:0);
   t.Write(q,token);var raw=t.Read(Math.Max(d.InputLength,33),token);var r=raw.AsSpan();if(r.Length>1&&r[1]==0x55)r=r[1..];
   if(r.Length<27||r[0]!=0x55||r[1]!=cmd||r[3]!=0||r[4]!=0)continue;
   var p=r[8..];if(p[17]>100||p[..19].ToArray().All(b=>b==0))continue;
   return new(p[17],p[18]==1);
  }
  return null;
 }
 private static List<PeripheralBatterySnapshot> QueryLogitech(HidBatteryDevice[] devices,HidBatteryModel model,CancellationToken token) {
  var result=new List<PeripheralBatterySnapshot>();
  var write=devices.FirstOrDefault(d=>d.Usage==1);var read=devices.FirstOrDefault(d=>d.Usage==2)??write;
  if(write is null||read is null)return result;
  try {
   using var w=new NativeHidTransport(write.Path);using var r=read.Path==write.Path?null:new NativeHidTransport(read.Path);var reader=r??w;
   byte[]? Send(byte slot,byte feature,byte function,byte a=0,byte b=0,byte c=0) {
    byte[] q=[0x10,slot,feature,(byte)((function<<4)|7),a,b,c];w.Write(Pad(q,Math.Max(7,write.OutputLength)),token);
    for(int i=0;i<3;i++) {var response=reader.Read(Math.Max(20,read.InputLength),token);if(response.Length>=7&&response[0] is 0x10 or 0x11&&response[1]==slot&&response[2]==feature&&response[3]==q[3])return response;if(response.Length>=5&&response[0]==0x8F&&response[1]==slot&&response[2]==feature&&response[3]==q[3])return null;}
    return null;
   }
   for(byte slot=1;slot<=model.Slots;slot++) {
    foreach(ushort feature in new ushort[]{0x1004,0x1000}) {
     token.ThrowIfCancellationRequested();byte[]? root;
     try { root=Send(slot,0,0,(byte)(feature>>8),(byte)feature); }catch(TimeoutException){break;}
     if(root is null||root[4]==0)continue;
     var battery=Send(slot,root[4],feature==0x1004?(byte)1:(byte)0);
     if(battery is null||battery[4]>100)continue;
     string name=$"Logitech HID++ 设备（槽位 {slot}）";var category=PeripheralBatteryCategory.Mouse;
     var nameFeature=Send(slot,0,0,0,5);
     if(nameFeature is not null&&nameFeature[4]!=0) {
      var type=Send(slot,nameFeature[4],2);if(type is not null&&type[4] is 0 or 1)category=PeripheralBatteryCategory.Keyboard;
      var length=Send(slot,nameFeature[4],0);
      if(length is not null&&length[4] is >0 and <80) {
       var chars=new List<byte>();while(chars.Count<length[4]) {var chunk=Send(slot,nameFeature[4],1,(byte)chars.Count);if(chunk is null)break;chars.AddRange(chunk.Skip(4).Take(Math.Min(chunk.Length-4,length[4]-chars.Count)));}
       if(chars.Count==length[4])name=System.Text.Encoding.UTF8.GetString(chars.ToArray());
      }
     }
     bool levelOnly=feature==0x1004&&battery[4]==0&&battery[5]!=0;
     var level=levelOnly?battery[5] switch {8=>"Full",4=>"Good",2=>"Low",1=>"Critical",_=>"Unknown"}:null;
     result.Add(new($"hid:{write.PhysicalId}:{category.ToString().ToLowerInvariant()}:slot{slot}",name,category,true,levelOnly?null:battery[4],battery[6] is >=1 and <=4,DateTimeOffset.UtcNow,"HID Logitech HID++","上游协议适配；GlassDesk 未逐型号实测",levelOnly?"设备仅提供电量档位":"",slot,write.PhysicalId,Level:level));break;
    }
   }
  } catch(OperationCanceledException){throw;}catch(Exception ex) when(ex is TimeoutException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
  return result;
 }
}
