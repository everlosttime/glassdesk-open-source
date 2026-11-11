using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
namespace GlassDesk.Platform.Battery;
internal sealed record HidBatteryDevice(string Path,string PhysicalId,string Name,ushort VendorId,ushort ProductId,ushort UsagePage,ushort Usage,int InterfaceNumber,int InputLength,int OutputLength,int FeatureLength);
/// <summary>Native HID transport. Each handle permits vendor app sharing, each request has a bounded overlapped wait.</summary>
internal sealed class NativeHidTransport : IDisposable
{
 private readonly SafeFileHandle _handle;
 internal NativeHidTransport(string path) {
  _handle=CreateFile(path,0xC0000000,3,0,3,0x40000000,0);
  if(_handle.IsInvalid) { _handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
 }
 internal static IReadOnlyList<HidBatteryDevice> Enumerate() {
  HidD_GetHidGuid(out var guid);
  nint set=SetupDiGetClassDevs(ref guid,null,0,0x12);
  if(set==-1) return [];
  var result=new List<HidBatteryDevice>();
  try {
   for(uint index=0;;index++) {
    var data=new InterfaceData{Size=Marshal.SizeOf<InterfaceData>()};
    if(!SetupDiEnumDeviceInterfaces(set,0,ref guid,index,ref data)) break;
    _=SetupDiGetDeviceInterfaceDetail(set,ref data,0,0,out uint size,0);
    if(size<8 || size>65536) continue;
    var detail=Marshal.AllocHGlobal((int)size);
    try {
     Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
     var devinfo=new DeviceInfoData{Size=Marshal.SizeOf<DeviceInfoData>()};
     if(!SetupDiGetDeviceInterfaceDetailWithInfo(set,ref data,detail,size,out _,ref devinfo)) continue;
     var path=Marshal.PtrToStringUni(detail+4)!;
     using var handle=CreateFile(path,0,3,0,3,0,0);
     if(handle.IsInvalid) continue;
     var attributes=new Attributes{Size=Marshal.SizeOf<Attributes>()};
     if(!HidD_GetAttributes(handle,ref attributes) || !HidD_GetPreparsedData(handle,out var preparsed)) continue;
     try {
      if(HidP_GetCaps(preparsed,out var caps)<0) continue;
      var name=new byte[512];
      string product=HidD_GetProductString(handle,name,name.Length)?Encoding.Unicode.GetString(name).TrimEnd('\0'):"HID 设备";
      // Container ID joins only sibling collections. Never join by VID:PID or product name.
      var key=new PropertyKey{Format=new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),Id=2};
      var container=new byte[16];
      var physical=SetupDiGetDeviceProperty(set,ref devinfo,ref key,out _,container,16,out _,0)?new Guid(container).ToString("D"):NormalizeCollectionPath(path);
      int iface=-1;
      var match=System.Text.RegularExpressions.Regex.Match(path,"&mi_([0-9a-f]{2})",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      if(match.Success) iface=Convert.ToInt32(match.Groups[1].Value,16);
      result.Add(new(path,physical,product,attributes.VendorId,attributes.ProductId,caps.UsagePage,caps.Usage,iface,caps.Input,caps.Output,caps.Feature));
     } finally { HidD_FreePreparsedData(preparsed); }
    } finally { Marshal.FreeHGlobal(detail); }
   }
  } finally { SetupDiDestroyDeviceInfoList(set); }
  return result;
 }
 internal static string NormalizeCollectionPath(string path)=>System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(),"&col[0-9a-f]{2}","");
 internal byte[] Feature(byte[] request,int responseLength,CancellationToken token,int waitMilliseconds=0) {
  Execute(request,false,0xB0191,token);
  if(waitMilliseconds>0&&token.WaitHandle.WaitOne(waitMilliseconds))token.ThrowIfCancellationRequested();
  var response=new byte[responseLength]; response[0]=request[0];
  return Execute(response,true,0xB0192,token);
 }
 internal byte[] GetFeature(byte reportId,int length,CancellationToken token) { var data=new byte[length];data[0]=reportId;return Execute(data,true,0xB0192,token); }
 internal void Write(byte[] request,CancellationToken token)=>Execute(request,false,0,token);
 internal byte[] Read(int length,CancellationToken token)=>Execute(new byte[length],true,0,token);
 private unsafe byte[] Execute(byte[] data,bool read,uint ioctl,CancellationToken token) {
  token.ThrowIfCancellationRequested();
  using var signal=new EventWaitHandle(false,EventResetMode.ManualReset);
  NativeOverlapped overlapped=default;overlapped.EventHandle=signal.SafeWaitHandle.DangerousGetHandle();
  fixed(byte* buffer=data) {
   bool ok=ioctl!=0?DeviceIoControl(_handle,ioctl,buffer,(uint)data.Length,buffer,(uint)data.Length,out _,&overlapped)
    : read?ReadFile(_handle,buffer,(uint)data.Length,out _,&overlapped):WriteFile(_handle,buffer,(uint)data.Length,out _,&overlapped);
   if(!ok && Marshal.GetLastWin32Error()!=997) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   var deadline=Environment.TickCount64+800;
   bool canceled=false;
   if(!ok) {
    while(!signal.WaitOne(20)) {
     if(token.IsCancellationRequested || Environment.TickCount64>=deadline) { canceled=true;_=CancelIoEx(_handle,&overlapped); break; }
    }
   }
   // Complete cancellation before unpinning memory or closing the event/handle.
   ok=GetOverlappedResult(_handle,&overlapped,out uint count,true);
   if(canceled) { token.ThrowIfCancellationRequested(); throw new TimeoutException("HID battery query timed out"); }
   if(!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   return read?data.AsSpan(0,(int)Math.Min(count,(uint)data.Length)).ToArray():[];
  }
 }
 public void Dispose()=>_handle.Dispose();
 [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size;public Guid Guid;public int Flags;public nint Reserved; }
 [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData { public int Size;public Guid ClassGuid;public uint DevInst;public nint Reserved; }
 [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size;public ushort VendorId,ProductId,Version; }
 [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format;public uint Id; }
 [StructLayout(LayoutKind.Sequential)] private struct Caps { public ushort Usage,UsagePage,Input,Output,Feature;[MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;public ushort LinkCollections,InputButtons,InputValues,InputIndices,OutputButtons,OutputValues,OutputIndices,FeatureButtons,FeatureValues,FeatureIndices; }
 [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
 [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle,ref Attributes attributes);
 [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out nint data);
 [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(nint data);
 [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint data,out Caps caps);
 [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle,byte[] data,int length);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint creation,uint flags,nint template);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,EntryPoint="SetupDiGetClassDevsW")] private static extern nint SetupDiGetClassDevs(ref Guid guid,string? enumerator,nint parent,uint flags);
 [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(nint set,nint dev,ref Guid guid,uint index,ref InterfaceData data);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,EntryPoint="SetupDiGetDeviceInterfaceDetailW")] private static extern bool SetupDiGetDeviceInterfaceDetail(nint set,ref InterfaceData data,nint detail,uint size,out uint required,nint dev);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,EntryPoint="SetupDiGetDeviceInterfaceDetailW")] private static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(nint set,ref InterfaceData data,nint detail,uint size,out uint required,ref DeviceInfoData dev);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,EntryPoint="SetupDiGetDevicePropertyW")] private static extern bool SetupDiGetDeviceProperty(nint set,ref DeviceInfoData dev,ref PropertyKey key,out uint type,byte[] data,uint size,out uint required,uint flags);
 [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern unsafe bool DeviceIoControl(SafeFileHandle handle,uint code,byte* input,uint inputLength,byte* output,uint outputLength,out uint count,NativeOverlapped* overlapped);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern unsafe bool ReadFile(SafeFileHandle handle,byte* buffer,uint length,out uint count,NativeOverlapped* overlapped);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern unsafe bool WriteFile(SafeFileHandle handle,byte* buffer,uint length,out uint count,NativeOverlapped* overlapped);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern unsafe bool CancelIoEx(SafeFileHandle handle,NativeOverlapped* overlapped);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern unsafe bool GetOverlappedResult(SafeFileHandle handle,NativeOverlapped* overlapped,out uint count,bool wait);
}
