using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage.Streams;
namespace GlassDesk.Platform.Battery;
/// <summary>Driver properties and the Bluetooth SIG Battery Service. No pairing or connection requests.</summary>
internal sealed class WindowsPeripheralBatteryProvider : IPeripheralBatteryProvider
{
 internal static int? BluetoothPropertyPercent(IReadOnlyDictionary<string,object> properties,string? container,IEnumerable<(string Id,string? Container,int? Percent)> nodes)
  =>GetPercent(properties)??(container is null?null:nodes.Where(n=>n.Id.StartsWith("BTH",StringComparison.OrdinalIgnoreCase)&&string.Equals(n.Container,container,StringComparison.OrdinalIgnoreCase)).Select(n=>n.Percent).FirstOrDefault(p=>p is >=0 and <=100));
 private static readonly string[] Properties=["System.Devices.ContainerId","System.Devices.Aep.ContainerId","System.Devices.Aep.IsConnected","System.Devices.BatteryLife","{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"];
 internal IReadOnlyCollection<PeripheralBatteryCategory>? Categories {get;set;}
 public async Task<IReadOnlyList<PeripheralBatterySnapshot>> QueryAsync(CancellationToken cancellationToken) {
  var result=new List<PeripheralBatterySnapshot>();
  var hid=await Task.Run(NativeHidTransport.Enumerate,cancellationToken).ConfigureAwait(false);
  var audio=await ReadAudioContainers(cancellationToken).ConfigureAwait(false);
  foreach(var group in hid.GroupBy(d=>d.PhysicalId,StringComparer.OrdinalIgnoreCase)) {
   var categories=CategoriesForPhysicalDevice(group.ToArray(),audio.ContainsKey(group.Key));
   foreach(var category in categories.Where(c=>Categories is null||Categories.Contains(c))) {
    var d=group.First();result.Add(new($"hid:{d.PhysicalId}:{category.ToString().ToLowerInvariant()}",DisplayName(group.ToArray()),category,true,null,null,DateTimeOffset.UtcNow,"Windows HID","未知或未公开电量协议","Windows 未提供电量；设备或当前连接方式可能不支持",PhysicalDeviceId:d.PhysicalId,IsDefaultAudio:audio.GetValueOrDefault(d.PhysicalId)));
   }
  }
  // Query PnP driver battery properties only on the corresponding physical node; no friendly-name joins.
  var nodes=await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True",Properties,DeviceInformationKind.Device).AsTask(cancellationToken).ConfigureAwait(false);
  foreach(var node in nodes) {
   if(GetPercent(node.Properties) is not {} percent)continue;
   var container=Container(node.Properties);if(container is null)continue;
   for(int index=0;index<result.Count;index++) {
    var d=result[index];if(!string.Equals(d.PhysicalDeviceId,container,StringComparison.OrdinalIgnoreCase))continue;
    // Never transfer a Bluetooth node's possibly cached percentage into the USB connection.
    if(node.Id.StartsWith("BTH",StringComparison.OrdinalIgnoreCase))continue;
    result[index]=d with{Percent=percent,Source="Windows 电池属性",Support="Windows 驱动提供",Reason=""};
   }
  }
  var classic=await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),Properties).AsTask(cancellationToken).ConfigureAwait(false);
  foreach(var info in classic) {
   cancellationToken.ThrowIfCancellationRequested();
   try {
    using var device=await BluetoothDevice.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
    if(device is null)continue;
    uint cod=device.ClassOfDevice.RawValue;int major=(int)(cod>>8)&31,minor=(int)(cod>>2)&63;
    var category=major==4?PeripheralBatteryCategory.Headset:major==5&&(minor&0x10)!=0?PeripheralBatteryCategory.Keyboard:major==5&&(minor&0x20)!=0?PeripheralBatteryCategory.Mouse:(PeripheralBatteryCategory?)null;
    var container=Container(info.Properties);if(category is null&&container is not null)category=FindCategory(container,hid,audio);
    if(category is null||Categories is not null&&!Categories.Contains(category.Value))continue;
    bool connected=device.ConnectionStatus==BluetoothConnectionStatus.Connected;
    var percent=BluetoothPropertyPercent(info.Properties,container,nodes.Select(n=>(n.Id,Container(n.Properties),GetPercent(n.Properties))));
    result.Add(new($"bt:{device.BluetoothAddress:X12}:{category.Value.ToString().ToLowerInvariant()}",info.Name,category.Value,connected,connected?percent:null,null,DateTimeOffset.UtcNow,"Windows 蓝牙电池属性","Windows 驱动提供",!connected?"蓝牙设备未连接":percent.HasValue?"":"蓝牙驱动未提供电量",PhysicalDeviceId:container,IsDefaultAudio:container is not null&&audio.GetValueOrDefault(container)));
   }catch(OperationCanceledException){throw;}catch(Exception ex)when(ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
  }
  var ble=await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),Properties).AsTask(cancellationToken).ConfigureAwait(false);
  foreach(var info in ble) {
   cancellationToken.ThrowIfCancellationRequested();
   try {
    using var device=await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);if(device is null)continue;
    var container=Container(info.Properties);var category=device.Appearance.RawValue switch {0x03C1=>PeripheralBatteryCategory.Keyboard,0x03C2=>PeripheralBatteryCategory.Mouse,_=>(PeripheralBatteryCategory?)null};
    if(category is null&&container is not null)category=FindCategory(container,hid,audio);
    if(category is null||Categories is not null&&!Categories.Contains(category.Value))continue;
    bool connected=device.ConnectionStatus==BluetoothConnectionStatus.Connected;int? percent=null;bool propertySource=false;
    if(connected) {
     percent=BluetoothPropertyPercent(info.Properties,container,nodes.Select(n=>(n.Id,Container(n.Properties),GetPercent(n.Properties))));
     propertySource=percent.HasValue;
     if(percent is null) {
     var services=await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery,BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
     foreach(var service in services.Services) {
      using(service) {
       var characteristics=await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
       foreach(var characteristic in characteristics.Characteristics) {
        var value=await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
        if(value.Status!=GattCommunicationStatus.Success||value.Value.Length!=1)continue;
        using var reader=DataReader.FromBuffer(value.Value);int raw=reader.ReadByte();if(raw<=100)percent=raw;
       }
      }
     }
     }
    }
    string key=$"bt:{device.BluetoothAddress:X12}:{category.Value.ToString().ToLowerInvariant()}";
    result.Add(new(key,info.Name,category.Value,connected,percent,null,DateTimeOffset.UtcNow,propertySource?"Windows 蓝牙电池属性":"BLE 标准电池服务 180F/2A19",propertySource?"Windows 驱动提供":"Bluetooth SIG 标准",!connected?"蓝牙设备未连接":percent.HasValue?"":"设备未提供可读取的标准电量",PhysicalDeviceId:container,IsDefaultAudio:container is not null&&audio.GetValueOrDefault(container)));
   }catch(OperationCanceledException){throw;}catch(Exception ex)when(ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
  }
  return result;
 }
 internal static string DisplayName(HidBatteryDevice[] group) {
  if(group.Any(d=>d.VendorId==0x38B1&&d.ProductId==0x3412))return "iKF V11 Pro 系列（USB 接收器）";
  return group.First().Name;
 }
 internal static List<PeripheralBatteryCategory> CategoriesForPhysicalDevice(HidBatteryDevice[] group,bool audio) {
  if(audio||group.Any(d=>d.VendorId==0x38B1&&d.ProductId==0x3412))return [PeripheralBatteryCategory.Headset];
  if(group.Any(d=>d.VendorId==0x41E4&&d.ProductId==0x211A))return [PeripheralBatteryCategory.Keyboard];
  var known=group.Select(HidBatteryModels.Find).OfType<HidBatteryModel>().Where(m=>m.Protocol!="Logitech").Select(m=>m.Category).Distinct().ToList();
  if(known.Count>0)return known;
  var primary=group.Where(d=>d.InterfaceNumber is 0 or -1&&d.UsagePage==1&&d.Usage is 2 or 6).OrderBy(d=>d.Usage==2?1:0).FirstOrDefault()
   ??group.Where(d=>d.UsagePage==1&&d.Usage is 2 or 6).OrderBy(d=>d.InterfaceNumber).FirstOrDefault();
  return primary is null?[]:[primary.Usage==2?PeripheralBatteryCategory.Mouse:PeripheralBatteryCategory.Keyboard];
 }
 private static PeripheralBatteryCategory? FindCategory(string physical,IReadOnlyList<HidBatteryDevice> hid,Dictionary<string,bool> audio) {
  if(audio.ContainsKey(physical))return PeripheralBatteryCategory.Headset;
  var d=hid.FirstOrDefault(d=>string.Equals(d.PhysicalId,physical,StringComparison.OrdinalIgnoreCase)&&d.UsagePage==1&&d.Usage is 2 or 6);
  return d is null?null:d.Usage==2?PeripheralBatteryCategory.Mouse:PeripheralBatteryCategory.Keyboard;
 }
 internal static int? GetPercent(IReadOnlyDictionary<string,object> properties) {
  foreach(var key in new[]{"System.Devices.BatteryLife","{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"})
   if(properties.TryGetValue(key,out var value)&&value is not null) {try {int percent=Convert.ToInt32(value,System.Globalization.CultureInfo.InvariantCulture);if(percent is >=0 and <=100)return percent;}catch(Exception ex) when(ex is FormatException or InvalidCastException or OverflowException){} }
  return null;
 }
 private static string? Container(IReadOnlyDictionary<string,object> properties) {
  foreach(var key in new[]{"System.Devices.ContainerId","System.Devices.Aep.ContainerId"})
   if(properties.TryGetValue(key,out var value)&&Guid.TryParse(value?.ToString(),out var guid)&&guid!=Guid.Empty)return guid.ToString("D");
  return null;
 }
 private static async Task<Dictionary<string,bool>> ReadAudioContainers(CancellationToken token) {
  var result=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
  string defaultId=MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
  var devices=await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector(),Properties).AsTask(token).ConfigureAwait(false);
  foreach(var device in devices)if(Container(device.Properties) is {} container) result[container]=result.GetValueOrDefault(container)||string.Equals(device.Id,defaultId,StringComparison.OrdinalIgnoreCase);
  return result;
 }
}
