using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace GlassDesk.Platform;

public sealed record BluetoothDeviceOption(string Id, string Name, bool IsConnected, bool IsPaired);

/// <summary>读取 Windows 已配对的经典蓝牙设备及当前连接状态。</summary>
public static class BluetoothDeviceReader
{
    public static async Task<IReadOnlyList<BluetoothDeviceOption>> GetPairedDevicesAsync()
    {
        try
        {
            var classicTask = DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask();
            var lowEnergyTask = DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask();
            await Task.WhenAll(classicTask, lowEnergyTask);
            var devices = (await classicTask).Select(info => (Info: info, LowEnergy: false))
                .Concat((await lowEnergyTask).Select(info => (Info: info, LowEnergy: true)))
                .DistinctBy(candidate => candidate.Info.Id)
                .Take(30)
                .ToArray();
            var results = new List<BluetoothDeviceOption>();
            foreach (var candidate in devices)
            {
                var info = candidate.Info;
                IDisposable? device = null;
                try
                {
                    var connected = false;
                    if (candidate.LowEnergy)
                    {
                        var lowEnergyDevice = await BluetoothLEDevice.FromIdAsync(info.Id);
                        device = lowEnergyDevice;
                        connected = lowEnergyDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    }
                    else
                    {
                        var classicDevice = await BluetoothDevice.FromIdAsync(info.Id);
                        device = classicDevice;
                        connected = classicDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    }

                    results.Add(new BluetoothDeviceOption(
                        info.Id,
                        string.IsNullOrWhiteSpace(info.Name) ? "蓝牙设备" : info.Name,
                        connected,
                        true));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                    or System.Runtime.InteropServices.COMException)
                {
                    results.Add(new BluetoothDeviceOption(
                        info.Id,
                        string.IsNullOrWhiteSpace(info.Name) ? "蓝牙设备" : info.Name,
                        false,
                        true));
                }
                finally
                {
                    device?.Dispose();
                }
            }

            return results
                .GroupBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => group.OrderByDescending(device => device.IsConnected).First())
                .OrderByDescending(device => device.IsConnected)
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or TypeLoadException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }
}
