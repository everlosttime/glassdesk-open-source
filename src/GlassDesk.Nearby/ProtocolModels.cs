using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GlassDesk.Core.Contracts;

namespace GlassDesk.Nearby;

public sealed class NearbyOptions
{
    public required Func<X509Certificate2> LoadIdentity { get; init; }
    public required string DefaultReceiveDirectory { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public int FirstPort { get; init; } = 53317;
    public bool DiscoveryEnabled { get; init; } = true;
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyList<string> ForbiddenReceiveRoots { get; init; } = [];
}

internal sealed record DeviceInfo(string Alias, string Version, string? DeviceModel, string? DeviceType,
    string Fingerprint, int Port, string Protocol, bool Download = false, bool Announce = false);
internal sealed record WireFile(string Id, string FileName, long Size, string FileType, string? Sha256 = null)
{
    public NearbyFile ToFile() => new(Id, FileName, Size, FileType, Sha256: Sha256);
}
internal sealed record PrepareRequest(DeviceInfo Info, Dictionary<string, WireFile> Files);
internal sealed record PrepareResponse(string SessionId, Dictionary<string, string> Files);
internal static class NearbyJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
}
