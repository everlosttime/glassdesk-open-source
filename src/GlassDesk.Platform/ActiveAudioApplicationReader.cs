using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

public readonly record struct ActiveAudioApplicationSnapshot(
    bool Available,
    string Title,
    string ApplicationName,
    int ProcessId);

/// <summary>当播放软件不提供系统媒体会话时，从 Core Audio 找到正在发声的进程。</summary>
public static class ActiveAudioApplicationReader
{
    public static ActiveAudioApplicationSnapshot Read()
    {
        object? deviceEnumeratorObject = null;
        IMMDevice? device = null;
        object? managerObject = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            deviceEnumeratorObject = new AudioDeviceEnumerator();
            var deviceEnumerator = (IMMDeviceEnumerator)deviceEnumeratorObject;
            if (deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) != 0
                || device is null) return default;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, 0, out managerObject) != 0
                || managerObject is not IAudioSessionManager2 manager
                || manager.GetSessionEnumerator(out sessionEnumerator) != 0
                || sessionEnumerator.GetCount(out var count) != 0)
                return default;

            var candidates = new List<ActiveAudioApplicationSnapshot>();
            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl2? control = null;
                try
                {
                    if (sessionEnumerator.GetSession(index, out control) != 0 || control is null
                        || control.GetState(out var state) != 0 || state != AudioSessionState.Active
                        || control.GetProcessId(out var processId) != 0 || processId == 0
                        || processId == Environment.ProcessId)
                        continue;

                    using var process = Process.GetProcessById((int)processId);
                    if (process.HasExited || MediaSourceFilter.IsExcluded(process.ProcessName)) continue;
                    var appName = ResolveApplicationName(process);
                    if (MediaSourceFilter.IsExcluded(appName)) continue;
                    var windowTitle = SafeMainWindowTitle(process);
                    candidates.Add(new ActiveAudioApplicationSnapshot(
                        true,
                        string.IsNullOrWhiteSpace(windowTitle) ? $"{appName} 正在播放" : windowTitle,
                        appName,
                        (int)processId));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or COMException)
                {
                    // Audio sessions can disappear between enumeration and inspection.
                }
                finally
                {
                    ReleaseCom(control);
                }
            }

            return candidates
                .OrderByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return default;
        }
        finally
        {
            ReleaseCom(sessionEnumerator);
            ReleaseCom(managerObject);
            ReleaseCom(device);
            ReleaseCom(deviceEnumeratorObject);
        }
    }

    private static string ResolveApplicationName(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or NotSupportedException)
        {
        }

        return process.ProcessName;
    }

    private static string SafeMainWindowTitle(Process process)
    {
        try { return process.MainWindowTitle; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private static void ReleaseCom(object? value)
    {
        // Other audio readers may hold the same RCW; release only our acquisition.
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    private const uint ClsCtxAll = 23;
    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    private enum AudioSessionState { Inactive, Active, Expired }

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

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4")]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out nint sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out nint simpleAudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        [PreserveSig] int RegisterSessionNotification(nint sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(nint sessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint duckNotification);
        [PreserveSig] int UnregisterDuckNotification(nint duckNotification);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl2 sessionControl);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName(out nint displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, nint eventContext);
        [PreserveSig] int GetIconPath(out nint iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, nint eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(nint events);
        [PreserveSig] int UnregisterAudioSessionNotification(nint events);
        [PreserveSig] int GetSessionIdentifier(out nint sessionIdentifier);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint sessionInstanceIdentifier);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}

public enum MediaKeyCommand { Previous, PlayPause, Next }

public static class MediaKeySender
{
    public static bool Send(MediaKeyCommand command)
    {
        try
        {
            var virtualKey = command switch
            {
                MediaKeyCommand.Previous => (byte)0xB1,
                MediaKeyCommand.Next => (byte)0xB0,
                _ => (byte)0xB3,
            };
            KeybdEvent(virtualKey, 0, 0, 0);
            KeybdEvent(virtualKey, 0, 2, 0);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
}
