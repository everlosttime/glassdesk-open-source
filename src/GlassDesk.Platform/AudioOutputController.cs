using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);

/// <summary>Active Windows playback endpoints and the default media output.</summary>
public static class AudioOutputController
{
    public static string LastDiagnostic { get; private set; } = "尚未读取输出设备";
    public static IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        object? instance = null;
        IDeviceCollection? devices = null;
        IDevice? current = null;
        var result = new List<AudioOutputDevice>();
        try
        {
            instance = new AudioDeviceEnumerator();
            var enumerator = (IDeviceEnumerator)instance;
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out current) >= 0)
                _ = current.GetId(out defaultId);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            for (uint index = 0; index < count; index++)
            {
                IDevice? device = null;
                IPropertyStore? properties = null;
                try
                {
                    if (devices.Item(index, out device) < 0 || device.GetId(out var id) < 0) continue;
                    var name = "音频输出设备";
                    if (device.OpenPropertyStore(0, out properties) >= 0)
                    {
                        var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
                        if (properties.GetValue(ref key, out var value) >= 0)
                        {
                            try
                            {
                                if (value.Type == 31) name = Marshal.PtrToStringUni(value.Pointer) ?? name;
                            }
                            finally { _ = PropVariantClear(ref value); }
                        }
                    }
                    result.Add(new(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally { Release(properties); Release(device); }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        { LastDiagnostic = ex.ToString(); return result; }
        finally { Release(current); Release(devices); Release(instance); }
        LastDiagnostic = $"已读取 {result.Count} 个声音输出设备";
        return result.DistinctBy(device => device.Id).OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static bool SetDefault(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !GetDevices().Any(device => device.Id == id)) return false;
        object? instance = null;
        try
        {
            // Windows' PolicyConfig ABI; isolated here because it is not a documented
            // MMDevice setter. Interface slot order: frgnca/AudioDeviceCmdlets IPolicyConfig.
            instance = new PolicyConfig();
            var policy = (IPolicyConfig)instance;
            if (policy.SetDefaultEndpoint(id, 0) < 0 || policy.SetDefaultEndpoint(id, 1) < 0) return false;
            return GetDevices().Any(device => device.Id == id && device.IsDefault);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException) { return false; }
        finally { Release(instance); }
    }

    private static void Release(object? value)
    {
        // Audio COM identities can share an RCW across polling/switching threads.
        // Balance this acquisition without invalidating another reader's references.
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private sealed class PolicyConfig;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
    }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint endpoint, nint mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, nint period, nint minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fx, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fx, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
