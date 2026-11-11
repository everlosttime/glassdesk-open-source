using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;

namespace GlassDesk.Nearby;

public sealed partial class NearbyTransferService
{
    private UdpClient? _udp;
    private Task? _discoveryLoop;
    private readonly SemaphoreSlim _announcement = new(1, 1);
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.167");
    private readonly DiscoveryThrottle _discoveryThrottle = new();
    internal Func<CancellationToken, Task<IReadOnlyList<(IPAddress Address, IPAddress Mask)>>> ReadInterfacesAsync { get; init; }
        = token => LocalNetwork.InterfacesAsync(token);

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            _udp = udp;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 53317));
            var interfaces = await ReadInterfacesAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(udp, _udp)) return;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (address, _) in interfaces)
            {
                try { udp.JoinMulticastGroup(MulticastGroup, address); }
                catch (SocketException) { }
            }
            _discoveryLoop = DiscoverAsync(cancellationToken);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !ReferenceEquals(udp, _udp)) { }
        catch (Exception error) when (error is SocketException or IOException)
        {
            udp?.Dispose(); if (ReferenceEquals(udp, _udp)) _udp = null;
            Update(s => s with { Error = "设备发现不可用。请检查防火墙，或手动输入局域网 IP。" });
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!State.Running) return;
        if (_udp is null && options.DiscoveryEnabled) { await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false); return; }
        var udp = _udp;
        var interfaces = await ReadInterfacesAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(udp, _udp) || !State.Running) return;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (address, _) in interfaces)
        {
            try { udp?.JoinMulticastGroup(MulticastGroup, address); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
        lock (_gate) _devices.Clear();
        _discoveryThrottle.Clear();
        Update(s => s with { Devices = [] });
        await AnnounceAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task AnnounceAsync(bool announce, CancellationToken cancellationToken)
    {
        await _announcement.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var udp = _udp;
            if (udp is null) return;
            var socket = udp.Client;
            if (socket is null) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Info(announce), NearbyJson.Options);
            var interfaces = await ReadInterfacesAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(udp, _udp)) return;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (address, _) in interfaces)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await udp.SendAsync(bytes, new IPEndPoint(MulticastGroup, 53317), cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !ReferenceEquals(udp, _udp)) { return; }
                catch (SocketException) { }
            }
        }
        finally { _announcement.Release(); }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _udp is not null)
            {
                var received = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (received.Buffer.Length > 8192 || !LocalNetwork.IsLocal(received.RemoteEndPoint.Address, AllowLoopback)) continue;
                DeviceInfo? info;
                try { info = JsonSerializer.Deserialize<DeviceInfo>(received.Buffer, NearbyJson.Options); }
                catch (JsonException) { continue; }
                if (info?.Fingerprint is not { Length: 64 } || !info.Fingerprint.All(Uri.IsHexDigit) || info.Protocol != "https" || info.Port is < 1 or > 65535
                    || info.Fingerprint.Equals(_fingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_discoveryThrottle.TryBegin(received.RemoteEndPoint.Address + ":" + info.Port, info.Fingerprint, DateTimeOffset.UtcNow)) continue;
                if (info.Announce) await AnnounceAsync(false, cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    using var client = CreateClient(received.RemoteEndPoint.Address.ToString(), info.Port, info.Fingerprint);
                    using var response = await client.PostAsJsonAsync("register", Info(), NearbyJson.Options, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    var registered = await response.Content.ReadFromJsonAsync<DeviceInfo>(NearbyJson.Options, timeout.Token).ConfigureAwait(false);
                    if (registered is not null) Remember(registered with { Port = info.Port }, received.RemoteEndPoint.Address.ToString(), info.Fingerprint);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or ArgumentException) { }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task StopDiscoveryAsync()
    {
        _udp?.Dispose(); _udp = null;
        if (_discoveryLoop is not null) { await _discoveryLoop.ConfigureAwait(false); _discoveryLoop = null; }
    }
}
