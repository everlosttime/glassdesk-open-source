using GlassDesk.Platform.Battery;
using System.Text.Json;
if(!args.Contains("--hardware-probe",StringComparer.Ordinal)) {Console.Error.WriteLine("Opt-in read-only hardware query: --hardware-probe");return 2;}
if(args.Contains("--engine-request",StringComparer.Ordinal)) {
 Console.WriteLine(JsonSerializer.Serialize(new {schema=1,categories=new[]{"mouse","headset","keyboard"},inventory=NativeHidTransport.Enumerate().Select(d=>new{path=d.Path,physicalId=d.PhysicalId,vendorId=d.VendorId,productId=d.ProductId})}));return 0;
}
if(args.Contains("--compare-feature",StringComparer.Ordinal)) { FeatureComparison.Run(); return 0; }
if(args.Contains("--ikf-battery-query",StringComparer.Ordinal)) { IkfBatteryQuery.Run(); return 0; }
if(args.Contains("--mchose-headset-query",StringComparer.Ordinal)) { MchoseHeadsetQuery.Run(); return 0; }
if(args.Contains("--engine-lifecycle",StringComparer.Ordinal)) {
 var provider=new MyFinderBatteryProvider();provider.SetActive(true);
 try {
  using var engineDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(100));
  for(int round=0;round<2;round++) Console.WriteLine(JsonSerializer.Serialize((await provider.QueryAsync(engineDeadline.Token)).Select(d=>new{d.Name,d.Percent,d.Connected,d.Source,d.Level,d.Reason})));
 } finally {provider.SetActive(false);}
 return 0;
}
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
if(args.Contains("--native-details",StringComparer.Ordinal)) {
 Console.WriteLine(JsonSerializer.Serialize(NativeHidTransport.Enumerate().Where(d=>d.VendorId is 0x5253 or 0x41e4 or 0x38b1 or 0x291d or 0x3837).Select(d=>new {d.Name,d.VendorId,d.ProductId,d.InterfaceNumber,d.UsagePage,d.Usage,d.InputLength,d.OutputLength,d.FeatureLength,Model=HidBatteryModels.Find(d)?.Protocol}),new JsonSerializerOptions{WriteIndented=true}));
 var mchose=NativeHidTransport.Enumerate().FirstOrDefault(d=>d.VendorId==0x5253&&d.ProductId==0x1021&&d.UsagePage==0xFF01&&d.Usage==1);
 if(mchose is not null) {using var transport=new NativeHidTransport(mchose.Path);for(int attempt=0;attempt<4;attempt++) {var response=transport.Feature(BatteryProtocols.MchoseRequest(),21,timeout.Token,40);Console.WriteLine("MCHOSE battery response: "+Convert.ToHexString(response));}}
 var native=new NativeHidBatteryProvider();Console.WriteLine(JsonSerializer.Serialize((await native.QueryAsync(timeout.Token)).Select(d=>new{d.Name,d.Percent,d.Connected,d.Source,d.Reason}),new JsonSerializerOptions{WriteIndented=true}));
}
var snapshots=await PeripheralBatteryService.Shared.RefreshAsync(timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(snapshots.Select(d=>new { d.Name,d.Category,d.Connected,d.Percent,d.Charging,d.Source,d.Support,d.Reason,d.ReceiverSlot,d.Level,d.IsDefaultAudio}),new JsonSerializerOptions{WriteIndented=true}));
return 0;


internal static class FeatureComparison {
 [System.Runtime.InteropServices.DllImport("hid.dll",SetLastError=true)] [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)] private static extern bool HidD_SetFeature(Microsoft.Win32.SafeHandles.SafeFileHandle h,byte[] data,int count);
 [System.Runtime.InteropServices.DllImport("hid.dll",SetLastError=true)] [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)] private static extern bool HidD_GetFeature(Microsoft.Win32.SafeHandles.SafeFileHandle h,byte[] data,int count);
 internal static void Run() {
  var d=NativeHidTransport.Enumerate().FirstOrDefault(d=>d.VendorId==0x5253&&d.ProductId==0x1021&&d.UsagePage==0xff01&&d.Usage==1);
  if(d is null)return;
  using var h=File.OpenHandle(d.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
  for(int n=0;n<4;n++) {var q=BatteryProtocols.MchoseRequest();var ok=HidD_SetFeature(h,q,q.Length);Thread.Sleep(40);var r=new byte[21];r[0]=17;ok=HidD_GetFeature(h,r,r.Length)&&ok;Console.WriteLine($"SYNC FEATURE {ok}: {Convert.ToHexString(r)}");}
 }
}
internal static class IkfBatteryQuery {
 internal static void Run() {
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
  foreach(var d in NativeHidTransport.Enumerate().Where(d=>d.VendorId==0x38b1&&d.ProductId is 0x3412 or 0x3419&&d.UsagePage==0xff00&&d.Usage==1&&d.OutputLength==64)) {
   Console.WriteLine($"IKF interface {d.InterfaceNumber}, page {d.UsagePage:X4}, lengths {d.InputLength}/{d.OutputLength}/{d.FeatureLength}");
   try {
    using var transport=new NativeHidTransport(d.Path);
    var request=new byte[d.OutputLength];byte[] command=[2,0x55,0x80,0,6,0,0xff,0,2,0xfa,0x0c,0xaa];command.CopyTo(request,0);
    var connection=new byte[d.OutputLength];byte[] connectionCommand=[2,7,0x5a,0xaa,0xa5,0x81,0x97,1,10];connectionCommand.CopyTo(connection,0);transport.Write(connection,deadline.Token);try{Console.WriteLine("IKF CONNECTION RX: "+Convert.ToHexString(transport.Read(d.InputLength,deadline.Token)));}catch(TimeoutException){Console.WriteLine("IKF no receiver status reply");}
    var firmware=new byte[d.OutputLength];byte[] firmwareCommand=[2,0x55,0x80,0,5,0,0xff,0,1,0x0d,0xaa];firmwareCommand.CopyTo(firmware,0);transport.Write(firmware,deadline.Token);try{Console.WriteLine("IKF FIRMWARE RX: "+Convert.ToHexString(transport.Read(d.InputLength,deadline.Token)));}catch(TimeoutException){Console.WriteLine("IKF no headset firmware reply");}
    transport.Write(request,deadline.Token);
    for(int i=0;i<3;i++) {try {Console.WriteLine("IKF RX: "+Convert.ToHexString(transport.Read(d.InputLength,deadline.Token)));} catch(TimeoutException){Console.WriteLine("IKF no input reply");break;} }
    if(d.FeatureLength>=64)Console.WriteLine("IKF FEATURE: "+Convert.ToHexString(transport.GetFeature(3,d.FeatureLength,deadline.Token)));
   } catch(Exception ex) when(ex is not OutOfMemoryException) {Console.WriteLine(ex.GetType().Name+": "+ex.Message);}
  }
 }
}

internal static class MchoseHeadsetQuery {
 internal static void Run() {
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6));
  foreach(var d in NativeHidTransport.Enumerate().Where(d=>d.VendorId==0x291d&&d.ProductId==0x385d)) {
   Console.WriteLine($"MCHOSE V9 PRO interface {d.InterfaceNumber}, page {d.UsagePage:X4}, usage {d.Usage:X4}, lengths {d.InputLength}/{d.OutputLength}/{d.FeatureLength}");
   if(d.UsagePage!=0xff90||d.Usage!=1||d.InputLength!=64||d.OutputLength!=64)continue;
   using var transport=new NativeHidTransport(d.Path);
   var request=new byte[64];request[0]=0x55;request[1]=0x65;request[2]=1;
   transport.Write(request,deadline.Token);
   for(int i=0;i<6;i++) {
    try {var response=transport.Read(64,deadline.Token);Console.WriteLine("MCHOSE HEADSET RX: "+Convert.ToHexString(response));if(response.Length==64&&response[0]==0x55&&response[1]==0x65)break;}
    catch(TimeoutException) {Console.WriteLine("MCHOSE no input reply");break;}
   }
  }
 }
}
