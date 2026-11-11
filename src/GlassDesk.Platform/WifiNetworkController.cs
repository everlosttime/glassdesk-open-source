using System.Runtime.InteropServices;
using System.Text;

namespace GlassDesk.Platform;

public sealed record WifiNetworkOption(
    Guid InterfaceId,
    string Name,
    string ProfileName,
    int SignalQuality,
    bool IsConnected,
    bool HasSavedProfile);

/// <summary>通过桌面 Native Wi-Fi API 枚举附近网络并连接已有配置文件。</summary>
public static class WifiNetworkController
{
    public static async Task<IReadOnlyList<WifiNetworkOption>> ScanAndGetAvailableNetworksAsync()
    {
        await Task.Run(RequestScan);
        // Native Wi-Fi scan completes asynchronously; a short bounded delay lets the
        // service refresh its cache without blocking the GlassDesk UI thread.
        await Task.Delay(1200);
        return await Task.Run(GetAvailableNetworks);
    }

    public static IReadOnlyList<WifiNetworkOption> GetAvailableNetworks()
    {
        nint client = 0;
        nint interfacesPointer = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || client == 0) return [];
            if (WlanEnumInterfaces(client, 0, out interfacesPointer) != 0 || interfacesPointer == 0) return [];

            var count = Marshal.ReadInt32(interfacesPointer);
            var offset = 8;
            var interfaceSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var results = new Dictionary<string, WifiNetworkOption>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < count; index++)
            {
                var infoPointer = nint.Add(interfacesPointer, offset + index * interfaceSize);
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(infoPointer);
                AppendNetworks(client, info.InterfaceGuid, results);
            }

            return results.Values
                .OrderByDescending(network => network.IsConnected)
                .ThenByDescending(network => network.SignalQuality)
                .ThenBy(network => network.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or AccessViolationException or SEHException)
        {
            return [];
        }
        finally
        {
            if (interfacesPointer != 0) WlanFreeMemory(interfacesPointer);
            if (client != 0) _ = WlanCloseHandle(client, 0);
        }
    }

    public static bool Connect(WifiNetworkOption network)
    {
        if (!network.HasSavedProfile || string.IsNullOrWhiteSpace(network.ProfileName)) return false;
        nint client = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || client == 0) return false;
            var parameters = new WlanConnectionParameters
            {
                ConnectionMode = WlanConnectionMode.Profile,
                Profile = network.ProfileName,
                DesiredSsid = 0,
                DesiredBssidList = 0,
                BssType = Dot11BssType.Any,
                Flags = 0,
            };
            var interfaceId = network.InterfaceId;
            return WlanConnect(client, ref interfaceId, ref parameters, 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return false;
        }
        finally
        {
            if (client != 0) _ = WlanCloseHandle(client, 0);
        }
    }

    /// <summary>断开指定无线网卡当前连接的网络，不会删除已保存的网络配置。</summary>
    public static bool Disconnect(WifiNetworkOption network)
    {
        nint client = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || client == 0) return false;
            var interfaceId = network.InterfaceId;
            return WlanDisconnect(client, ref interfaceId, 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return false;
        }
        finally
        {
            if (client != 0) _ = WlanCloseHandle(client, 0);
        }
    }

    private static void RequestScan()
    {
        nint client = 0;
        nint interfacesPointer = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || client == 0) return;
            if (WlanEnumInterfaces(client, 0, out interfacesPointer) != 0 || interfacesPointer == 0) return;
            var count = Marshal.ReadInt32(interfacesPointer);
            var offset = 8;
            var interfaceSize = Marshal.SizeOf<WlanInterfaceInfo>();
            for (var index = 0; index < count; index++)
            {
                var infoPointer = nint.Add(interfacesPointer, offset + index * interfaceSize);
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(infoPointer);
                var interfaceId = info.InterfaceGuid;
                _ = WlanScan(client, ref interfaceId, 0, 0, 0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The cached list remains a valid fallback.
        }
        finally
        {
            if (interfacesPointer != 0) WlanFreeMemory(interfacesPointer);
            if (client != 0) _ = WlanCloseHandle(client, 0);
        }
    }

    private static void AppendNetworks(nint client, Guid interfaceId,
        Dictionary<string, WifiNetworkOption> results)
    {
        nint listPointer = 0;
        try
        {
            if (WlanGetAvailableNetworkList(client, ref interfaceId, 0, 0, out listPointer) != 0
                || listPointer == 0) return;
            var count = Marshal.ReadInt32(listPointer);
            var offset = 8;
            var itemSize = Marshal.SizeOf<WlanAvailableNetwork>();
            for (var index = 0; index < count; index++)
            {
                var itemPointer = nint.Add(listPointer, offset + index * itemSize);
                var item = Marshal.PtrToStructure<WlanAvailableNetwork>(itemPointer);
                var name = DecodeSsid(item.Ssid);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var option = new WifiNetworkOption(
                    interfaceId,
                    name,
                    item.ProfileName ?? string.Empty,
                    (int)Math.Clamp(item.SignalQuality, 0u, 100u),
                    (item.Flags & 1) != 0,
                    (item.Flags & 2) != 0 && !string.IsNullOrWhiteSpace(item.ProfileName));
                if (!results.TryGetValue(name, out var existing)
                    || option.IsConnected
                    || option.SignalQuality > existing.SignalQuality)
                    results[name] = option;
            }
        }
        finally
        {
            if (listPointer != 0) WlanFreeMemory(listPointer);
        }
    }

    private static string DecodeSsid(Dot11Ssid ssid)
    {
        if (ssid.Bytes is null || ssid.Length == 0) return string.Empty;
        var length = Math.Min((int)ssid.Length, ssid.Bytes.Length);
        try
        {
            return Encoding.UTF8.GetString(ssid.Bytes, 0, length).TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Default.GetString(ssid.Bytes, 0, length).TrimEnd('\0');
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved,
        out uint negotiatedVersion, out nint clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint clientHandle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(nint clientHandle, ref Guid interfaceGuid,
        uint flags, nint reserved, out nint availableNetworkList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(nint clientHandle, ref Guid interfaceGuid,
        nint dot11Ssid, nint ieData, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanConnect(nint clientHandle, ref Guid interfaceGuid,
        ref WlanConnectionParameters connectionParameters, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(nint clientHandle, ref Guid interfaceGuid, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanAvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11Ssid Ssid;
        public int BssType;
        public uint NumberOfBssids;
        [MarshalAs(UnmanagedType.Bool)] public bool NetworkConnectable;
        public uint NotConnectableReason;
        public uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] PhyTypes;
        [MarshalAs(UnmanagedType.Bool)] public bool MorePhyTypes;
        public uint SignalQuality;
        [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
        public int DefaultAuthAlgorithm;
        public int DefaultCipherAlgorithm;
        public uint Flags;
        public uint Reserved;
    }

    private enum WlanConnectionMode { Profile, TemporaryProfile, DiscoverySecure, DiscoveryUnsecure, Auto, Invalid }
    private enum Dot11BssType { Infrastructure = 1, Independent = 2, Any = 3 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public WlanConnectionMode ConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public nint DesiredSsid;
        public nint DesiredBssidList;
        public Dot11BssType BssType;
        public uint Flags;
    }
}
