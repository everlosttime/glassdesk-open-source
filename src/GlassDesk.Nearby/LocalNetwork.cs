using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GlassDesk.Nearby;

internal static class LocalNetwork
{
    // Async callers can run synchronously through their first completed await.
    // Windows adapter enumeration must never occupy the UI thread in that case.
    internal static Task<IReadOnlyList<(IPAddress Address, IPAddress Mask)>> InterfacesAsync(CancellationToken cancellationToken,
        Func<IReadOnlyList<(IPAddress Address, IPAddress Mask)>>? read = null)
        => Task.Run(read ?? Interfaces, cancellationToken);

    internal static IReadOnlyList<(IPAddress Address, IPAddress Mask)> Interfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null)
        .Select(a => (a.Address, a.IPv4Mask)).ToArray();

    internal static bool IsLocal(IPAddress address, bool allowLoopback)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        var remote = address.GetAddressBytes();
        if (remote[0] is 0 or >= 224 || remote.All(b => b == 255)) return false;
        return Interfaces().Any(local =>
        {
            var bytes = local.Address.GetAddressBytes(); var mask = local.Mask.GetAddressBytes();
            var same = true; var broadcast = true; var network = true;
            for (var i = 0; i < 4; i++)
            {
                same &= (bytes[i] & mask[i]) == (remote[i] & mask[i]);
                broadcast &= (remote[i] | mask[i]) == 255;
                network &= (remote[i] & ~mask[i]) == 0;
            }
            return same && !broadcast && !network;
        });
    }
}
