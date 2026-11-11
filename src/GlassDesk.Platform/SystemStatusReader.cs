using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

/// <summary>桌面信息栏需要的轻量系统状态快照。</summary>
public readonly record struct SystemStatusSnapshot(
    int? BatteryPercent,
    bool IsCharging,
    string NetworkLabel,
    int? VolumePercent,
    bool IsMuted)
{
    // An unavailable percentage is different from a machine with no battery.
    public bool HasBattery { get; init; } = true;
}

/// <summary>适合按秒刷新、无需 WMI 或性能计数器服务的轻量性能快照。</summary>
public readonly record struct SystemPerformanceSnapshot(int? CpuPercent, int? MemoryPercent);

/// <summary>通过 Windows 本机接口读取电源、网络和默认播放设备音量。</summary>
public static class SystemStatusReader
{
    public static string LastAudioDiagnostic { get; private set; } = "尚未读取音量";
    private static readonly object CpuGate = new();
    private static ulong? _previousIdle;
    private static ulong? _previousKernel;
    private static ulong? _previousUser;

    public static SystemStatusSnapshot Read()
    {
        var battery = ReadBattery();
        var network = ReadNetworkLabel();
        var volume = ReadVolume();
        return new SystemStatusSnapshot(battery.Percent, battery.Charging, network, volume.Percent, volume.Muted)
        { HasBattery = battery.HasBattery };
    }

    public static SystemPerformanceSnapshot ReadPerformance()
        => new(ReadCpuPercent(), ReadMemoryPercent());

    /// <summary>设置默认播放设备的主音量。传入值会被限制在 0–100。</summary>
    public static bool SetVolumePercent(int percent)
        => WithAudioEndpoint(endpoint =>
            endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, 0) == 0);

    /// <summary>设置默认播放设备的静音状态。</summary>
    public static bool SetMuted(bool muted)
        => WithAudioEndpoint(endpoint => endpoint.SetMute(muted, 0) == 0);

    private static int? ReadCpuPercent()
    {
        try
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;
            var idle = idleTime.ToUInt64();
            var kernel = kernelTime.ToUInt64();
            var user = userTime.ToUInt64();
            lock (CpuGate)
            {
                if (_previousIdle is not { } previousIdle
                    || _previousKernel is not { } previousKernel
                    || _previousUser is not { } previousUser)
                {
                    _previousIdle = idle;
                    _previousKernel = kernel;
                    _previousUser = user;
                    return null;
                }

                var idleDelta = idle - previousIdle;
                var totalDelta = (kernel - previousKernel) + (user - previousUser);
                _previousIdle = idle;
                _previousKernel = kernel;
                _previousUser = user;
                if (totalDelta == 0) return null;
                return Math.Clamp((int)Math.Round((totalDelta - Math.Min(idleDelta, totalDelta)) * 100d / totalDelta), 0, 100);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static int? ReadMemoryPercent()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? Math.Clamp((int)status.MemoryLoad, 0, 100) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static (int? Percent, bool Charging, bool HasBattery) ReadBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return (null, false, true);
            // BatteryFlag 128 means no system battery. Some desktop drivers still
            // return 100% in BatteryLifePercent; that value must not expose a battery.
            // 255 means unknown flags, not an authoritative no-battery report.
            if (status.BatteryFlag != byte.MaxValue && (status.BatteryFlag & 128) != 0)
                return (null, false, false);
            return (status.BatteryLifePercent == byte.MaxValue ? null : Math.Clamp((int)status.BatteryLifePercent, 0, 100),
                status.ACLineStatus == 1, true);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, false, true);
        }
    }

    private static string ReadNetworkLabel()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();
            if (active.Any(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) return "Wi-Fi";
            return active.Length > 0 && NetworkInterface.GetIsNetworkAvailable() ? "联网" : "离线";
        }
        catch (NetworkInformationException)
        {
            return "未知";
        }
    }

    private static (int? Percent, bool Muted) ReadVolume()
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            enumeratorObject = new AudioDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            if (hr != 0 || device is null)
            {
                LastAudioDiagnostic = $"GetDefaultAudioEndpoint: 0x{hr:X8}";
                return (null, false);
            }
            var iid = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref iid, ClsCtxAll, 0, out endpointObject);
            if (hr != 0 || endpointObject is not IAudioEndpointVolume endpoint)
            {
                LastAudioDiagnostic = $"Activate volume: 0x{hr:X8}";
                return (null, false);
            }
            hr = endpoint.GetMasterVolumeLevelScalar(out var scalar);
            if (hr != 0)
            {
                LastAudioDiagnostic = $"Read volume: 0x{hr:X8}";
                return (null, false);
            }
            hr = endpoint.GetMute(out var muted);
            if (hr != 0) { LastAudioDiagnostic = $"Read mute: 0x{hr:X8}"; return (null, false); }
            LastAudioDiagnostic = "音量读取成功";
            return ((int)Math.Round(Math.Clamp(scalar, 0, 1) * 100), muted);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            LastAudioDiagnostic = ex.ToString();
            return (null, false);
        }
        finally
        {
            ReleaseCom(endpointObject);
            ReleaseCom(device);
            ReleaseCom(enumeratorObject);
        }
    }

    private static bool WithAudioEndpoint(Func<IAudioEndpointVolume, bool> action)
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            enumeratorObject = new AudioDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) != 0 || device is null)
                return false;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsCtxAll, 0, out endpointObject) != 0
                || endpointObject is not IAudioEndpointVolume endpoint)
                return false;
            return action(endpoint);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            ReleaseCom(endpointObject);
            ReleaseCom(device);
            ReleaseCom(enumeratorObject);
        }
    }

    private static void ReleaseCom(object? value)
    {
        // Other audio readers may hold the same RCW; release only our acquisition.
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    private const uint ClsCtxAll = 23;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, nint eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, nint eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
