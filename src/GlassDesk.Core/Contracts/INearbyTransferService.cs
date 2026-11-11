using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Contracts;

public sealed record NearbyDevice(string Fingerprint, string Name, string Address, int Port)
{
    public override string ToString() => $"{Name} · {Address}:{Port}";
}

public sealed record NearbyFile(string Id, string Name, long Size, string ContentType, string? LocalPath = null, string? Sha256 = null);
public enum NearbyTaskState { Waiting, Transferring, Completed, Failed, Cancelled }
public sealed record NearbyTask(string Id, bool Receiving, string DeviceName, NearbyTaskState State,
    string CurrentFile, long Transferred, long Total, double BytesPerSecond, string? Error,
    IReadOnlyList<NearbyFile> FailedFiles, string? Directory);
public sealed record NearbyIncoming(string Id, string DeviceName, IReadOnlyList<NearbyFile> Files, string Directory, DateTimeOffset ExpiresAt);
public sealed record NearbyState(bool Running, string DeviceName, int Port, IReadOnlyList<NearbyDevice> Devices,
    NearbyTask? Sending, NearbyTask? Receiving, NearbyIncoming? Incoming, string? Error);

public interface INearbyTransferService : IAsyncDisposable
{
    NearbyState State { get; }
    event EventHandler? Changed;
    Task StartAsync(NearbySettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<NearbyDevice> AddDeviceAsync(string address, int port = 53317, CancellationToken cancellationToken = default);
    Task SendAsync(NearbyDevice device, IReadOnlyList<NearbyFile> files,
        Func<CancellationToken, Task<string?>>? requestPin = null, CancellationToken cancellationToken = default);
    Task DecideAsync(string requestId, bool accept, CancellationToken cancellationToken = default);
    Task CancelAsync(bool receiving, CancellationToken cancellationToken = default);
    Task InterruptAsync(string reason, CancellationToken cancellationToken = default);
}
