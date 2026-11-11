using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

public sealed record ApplicationVolume(string Key, string Name, string? ExecutablePath,
    string EndpointId, IReadOnlyList<string> SessionIds, double VolumePercent, bool IsMuted);

public sealed record ApplicationVolumeSnapshot(bool Available, IReadOnlyList<ApplicationVolume> Applications);

/// <summary>Controls existing sessions on the current multimedia output without changing endpoint volume.</summary>
public static class ApplicationVolumeController
{
    public static ApplicationVolumeSnapshot Read()
    {
        var sessions = new List<SessionVolume>();
        var available = VisitSessions(null, (endpoint, session, volume) =>
        {
            if (session.GetState(out var state) < 0 || state == 2 || session.GetProcessId(out var pid) < 0 ||
                session.GetSessionInstanceIdentifier(out var id) < 0 || string.IsNullOrEmpty(id) ||
                volume.GetMasterVolume(out var level) < 0 || volume.GetMute(out var muted) < 0) return;
            string? path = null;
            var system = session.IsSystemSoundsSession() == 0;
            var name = system ? "系统声音" : $"应用 {pid}";
            if (!system && pid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    name = process.ProcessName;
                    path = process.MainModule?.FileName;
                    var description = process.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) name = description;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            var key = system ? "system-sounds" : path ?? $"process:{pid}";
            sessions.Add(new(key, name, path, endpoint, id, Math.Clamp(level * 100d, 0, 100), muted));
        });
        return new(available, sessions.GroupBy(session => session.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApplicationVolume(group.Key, group.First().Name, group.First().Path,
                group.First().Endpoint, group.Select(session => session.Id).ToArray(),
                group.Average(session => session.Percent), group.All(session => session.Muted)))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public static bool SetVolume(ApplicationVolume application, double percent)
    {
        if (!double.IsFinite(percent)) return false;
        return Change(application, volume =>
        {
            var success = volume.SetMasterVolume((float)(Math.Clamp(percent, 0, 100) / 100d), nint.Zero) >= 0;
            if (percent > 0) success &= volume.SetMute(false, nint.Zero) >= 0;
            return success;
        });
    }

    public static bool SetMuted(ApplicationVolume application, bool muted) =>
        Change(application, volume => volume.SetMute(muted, nint.Zero) >= 0);

    private static bool Change(ApplicationVolume application, Func<ISimpleAudioVolume, bool> action)
    {
        if (string.IsNullOrEmpty(application.EndpointId) || application.SessionIds.Count == 0) return false;
        var ids = application.SessionIds.ToHashSet(StringComparer.Ordinal);
        var changed = 0;
        var success = true;
        var available = VisitSessions(application.EndpointId, (_, session, volume) =>
        {
            if (session.GetState(out var state) < 0 || state == 2 ||
                session.GetSessionInstanceIdentifier(out var id) < 0 || !ids.Contains(id)) return;
            changed++;
            success &= action(volume);
        });
        return available && success && changed > 0;
    }

    private static bool VisitSessions(string? expectedEndpoint, Action<string, IAudioSessionControl2, ISimpleAudioVolume> action)
    {
        object? instance = null;
        IDevice? device = null;
        object? managerObject = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            instance = new DeviceEnumerator();
            var enumerator = (IDeviceEnumerator)instance;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) < 0 || device.GetId(out var endpoint) < 0 ||
                (expectedEndpoint is not null && !string.Equals(endpoint, expectedEndpoint, StringComparison.Ordinal))) return false;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, nint.Zero, out managerObject) < 0 ||
                managerObject is not IAudioSessionManager2 manager || manager.GetSessionEnumerator(out sessions) < 0 ||
                sessions.GetCount(out var count) < 0) return false;
            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl2? control = null;
                try
                {
                    if (sessions.GetSession(index, out control) >= 0 && control is ISimpleAudioVolume volume)
                        action(endpoint, control, volume);
                }
                catch (COMException) { /* A session can end between enumeration and access. */ }
                finally { Release(control); }
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException) { return false; }
        finally { Release(sessions); Release(managerObject); Release(device); Release(instance); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    private sealed record SessionVolume(string Key, string Name, string? Path, string Endpoint, string Id, double Percent, bool Muted);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private sealed class DeviceEnumerator;
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out nint store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 control);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, nint context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, nint context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(nint events);
        [PreserveSig] int UnregisterAudioSessionNotification(nint events);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, nint context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
