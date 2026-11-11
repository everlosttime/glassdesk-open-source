using Windows.Devices.Radios;

namespace GlassDesk.Platform;

public readonly record struct SystemRadioState(bool Available, bool IsOn, bool CanControl);

/// <summary>读取并在系统允许时切换 Wi-Fi / 蓝牙无线电。</summary>
public static class SystemRadioController
{
    public static Task<SystemRadioState> ReadWifiAsync() => ReadAsync(RadioKind.WiFi);

    public static Task<SystemRadioState> ReadBluetoothAsync() => ReadAsync(RadioKind.Bluetooth);

    public static Task<bool> SetWifiAsync(bool enabled) => SetAsync(RadioKind.WiFi, enabled);

    public static Task<bool> SetBluetoothAsync(bool enabled) => SetAsync(RadioKind.Bluetooth, enabled);

    private static async Task<SystemRadioState> ReadAsync(RadioKind kind)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return new(false, false, false);
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(candidate => candidate.Kind == kind);
            return radio is null
                ? new(false, false, false)
                : new(true, radio.State == RadioState.On, true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or TypeLoadException or System.Runtime.InteropServices.COMException)
        {
            return new(false, false, false);
        }
    }

    private static async Task<bool> SetAsync(RadioKind kind, bool enabled)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return false;
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(candidate => candidate.Kind == kind);
            if (radio is null) return false;
            var result = await radio.SetStateAsync(enabled ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or TypeLoadException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
