using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

public sealed record NetworkTrafficCounter(string Id, string Name, long ReceivedBytes, long SentBytes);

public sealed record NetworkSpeedSnapshot(bool Connected, string AdapterName, double? DownloadBytesPerSecond, double? UploadBytesPerSecond);

/// <summary>Samples a single routed interface, avoiding double counting VPN and physical adapter traffic.</summary>
public sealed class NetworkSpeedReader
{
    private readonly object _gate = new();
    private NetworkTrafficCounter? _previous;
    private TimeSpan _previousAt;

    public NetworkSpeedSnapshot Read()
    {
        NetworkTrafficCounter? current = null;
        try
        {
            // This asks the local routing table only; it does not contact this address.
            var routeFound = GetBestInterface(0x01010101, out var routeIndex) == 0;
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
            var adapter = routeFound ? adapters.FirstOrDefault(candidate =>
                candidate.Supports(NetworkInterfaceComponent.IPv4) &&
                candidate.GetIPProperties().GetIPv4Properties()?.Index == routeIndex) : null;
            // IPv6-only/default-gateway fallback. Still choose one interface, never sum
            // a VPN's counters together with the underlying network's counters.
            adapter ??= adapters.FirstOrDefault(candidate => candidate.GetIPProperties().GatewayAddresses
                .Any(gateway => !gateway.Address.Equals(System.Net.IPAddress.Any) &&
                    !gateway.Address.Equals(System.Net.IPAddress.IPv6Any)));
            if (adapter is not null)
            {
                var statistics = adapter.GetIPStatistics();
                current = new(adapter.Id, adapter.Name, statistics.BytesReceived, statistics.BytesSent);
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return Sample(current, TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));
    }

    public void Reset()
    {
        lock (_gate) _previous = null;
    }

    /// <summary>Calculates rates from monotonic timestamps; reconnects and counter resets establish a new baseline.</summary>
    public NetworkSpeedSnapshot Sample(NetworkTrafficCounter? current, TimeSpan now)
    {
        lock (_gate)
        {
            var previous = _previous;
            var seconds = (now - _previousAt).TotalSeconds;
            _previous = current;
            _previousAt = now;
            if (current is null) return new(false, string.Empty, null, null);
            if (previous is null || previous.Id != current.Id || seconds <= 0 || seconds > 5 ||
                current.ReceivedBytes < previous.ReceivedBytes || current.SentBytes < previous.SentBytes)
                return new(true, current.Name, null, null);
            return new(true, current.Name, (current.ReceivedBytes - previous.ReceivedBytes) / seconds,
                (current.SentBytes - previous.SentBytes) / seconds);
        }
    }

    public static string FormatRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value < 0) return "--";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destinationAddress, out int interfaceIndex);
}
