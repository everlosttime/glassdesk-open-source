using System.Net;
using System.Security.Cryptography.X509Certificates;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace GlassDesk.Nearby;

public sealed partial class NearbyTransferService(NearbyOptions options) : INearbyTransferService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, NearbyDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private NearbyState _state = new(false, "", 0, [], null, null, null, null);
    private WebApplication? _server;
    private X509Certificate2? _identity;
    private CancellationTokenSource? _lifetime, _sending;
    private Task? _sendWork;
    private ReceiveSession? _receive;
    private string _receiveDirectory = options.DefaultReceiveDirectory;
    private string _fingerprint = "";
    private string? _sendInterruption;
    public NearbyState State { get { lock (_gate) return _state; } }
    public event EventHandler? Changed;
    private bool AllowLoopback => IPAddress.IsLoopback(options.BindAddress);

    private void Update(Func<NearbyState, NearbyState> change)
    {
        lock (_gate) _state = change(_state);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DeviceInfo Info(bool announce = false) => new(State.DeviceName, "2.2", "GlassDesk / Windows", "desktop", _fingerprint, State.Port, "https", Announce: announce);

    public async Task StartAsync(NearbySettings settings, CancellationToken cancellationToken = default)
    {
        // Reserve lifecycle order before scheduling work, so an immediate Stop
        // still waits for this Start instead of overtaking a queued worker.
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() => StartCoreAsync(settings, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(NearbySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (_server is not null) return;
            if (settings.ReceiveDirectory is not null && !Path.IsPathFullyQualified(settings.ReceiveDirectory))
                throw new ArgumentException("接收目录需要完整路径。");
            _receiveDirectory = Path.GetFullPath(settings.ReceiveDirectory ?? options.DefaultReceiveDirectory);
            foreach (var forbidden in options.ForbiddenReceiveRoots)
            {
                var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
                var destination = _receiveDirectory.TrimEnd(Path.DirectorySeparatorChar);
                if (destination.Equals(root, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || root.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("接收目录不能与 GlassDesk Data 目录重叠，请选择下载目录或其他独立文件夹。");
            }
            _identity = options.LoadIdentity();
            _fingerprint = CertificateIdentity.Fingerprint(_identity);
            _lifetime = new CancellationTokenSource();
            Update(s => s with { DeviceName = settings.DeviceName, Error = null });
            Exception? bindError = null;
            for (var port = options.FirstPort; port <= options.FirstPort + 10 && port <= 65535; port++)
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
                // No HTTP request logging: upload URLs contain authorization tokens.
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.AddServerHeader = false;
                    kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
                    kestrel.Limits.MinRequestBodyDataRate = null;
                    kestrel.Limits.MaxConcurrentConnections = 32;
                    kestrel.Listen(options.BindAddress, port, listener => listener.UseHttps(https =>
                    {
                        https.ServerCertificate = _identity;
                        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        https.ClientCertificateValidation = (certificate, _, _) => CertificateIdentity.Validate(certificate);
                    }));
                });
                var app = builder.Build();
                app.Use(async (context, next) =>
                {
                    if (context.Connection.RemoteIpAddress is not { } address || !LocalNetwork.IsLocal(address, AllowLoopback))
                    { context.Response.StatusCode = 403; return; }
                    try { await next(context).ConfigureAwait(false); }
                    catch (System.Text.Json.JsonException) { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
                    catch (BadHttpRequestException) { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                });
                app.MapGet("/api/localsend/v2/info", () => Results.Json(Info(), NearbyJson.Options));
                app.MapPost("/api/localsend/v2/register", (Func<HttpContext, Task<IResult>>)RegisterAsync);
                app.MapPost("/api/localsend/v2/prepare-upload", (Func<HttpContext, Task<IResult>>)PrepareAsync);
                app.MapPost("/api/localsend/v2/upload", (Func<HttpContext, Task<IResult>>)UploadAsync);
                app.MapPost("/api/localsend/v2/cancel", (Func<HttpContext, Task<IResult>>)RemoteCancelAsync);
                try
                {
                    await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    _server = app;
                    Update(s => s with { Running = true, Port = port });
                    if (options.DiscoveryEnabled) await StartDiscoveryAsync(_lifetime.Token).ConfigureAwait(false);
                    return;
                }
                catch (IOException error) { bindError = error; await app.DisposeAsync().ConfigureAwait(false); }
            }
            throw new IOException("附近传输端口均被占用（53317–53327）。", bindError);
        }
        catch (Exception error)
        {
            if (_server is not null) { await _server.DisposeAsync().ConfigureAwait(false); _server = null; }
            _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
            _identity?.Dispose(); _identity = null;
            Update(s => s with { Running = false, Port = 0, Error = "附近传输未能开启：" + error.Message });
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lifetime?.Cancel();
            await CancelAsync(false, CancellationToken.None).ConfigureAwait(false);
            await CancelAsync(true, CancellationToken.None).ConfigureAwait(false);
            await StopDiscoveryAsync().ConfigureAwait(false);
            if (_server is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _server.StopAsync(timeout.Token).ConfigureAwait(false); }
                finally { await _server.DisposeAsync().ConfigureAwait(false); _server = null; }
            }
            _identity?.Dispose(); _identity = null;
            _lifetime?.Dispose(); _lifetime = null;
            lock (_gate) _devices.Clear();
            Update(s => s with { Running = false, Port = 0, Devices = [], Incoming = null, Error = null });
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<IResult> RegisterAsync(HttpContext context)
    {
        var info = await context.Request.ReadFromJsonAsync<DeviceInfo>(NearbyJson.Options, context.RequestAborted).ConfigureAwait(false);
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted).ConfigureAwait(false);
        if (info is null || certificate is null || info.Protocol != "https" || info.Port is < 1 or > 65535) return Results.BadRequest();
        Remember(info, context.Connection.RemoteIpAddress!.ToString(), CertificateIdentity.Fingerprint(certificate));
        return Results.Json(Info(), NearbyJson.Options);
    }

    private void Remember(DeviceInfo info, string address, string fingerprint)
    {
        if (fingerprint.Equals(_fingerprint, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(info.Alias) || info.Alias.Length > 80 || info.Version?.StartsWith("2.", StringComparison.Ordinal) != true) return;
        lock (_gate)
        {
            if (_devices.TryGetValue(fingerprint, out var known) && known.Name == info.Alias && known.Port == info.Port) return;
            if (_devices.Count >= 256 && !_devices.ContainsKey(fingerprint)) return;
            _devices[fingerprint] = new(fingerprint, info.Alias, address, info.Port);
        }
        Update(s => s with { Devices = _devices.Values.ToArray() });
    }

    public async Task CancelAsync(bool receiving, CancellationToken cancellationToken = default)
    {
        if (receiving)
        {
            ReceiveSession? session;
            lock (_gate) session = _receive;
            if (session is not null) await FinishReceiveAsync(session, NearbyTaskState.Cancelled, "已取消接收。").ConfigureAwait(false);
        }
        else
        {
            Task? work;
            lock (_gate) { _sending?.Cancel(); work = _sendWork; }
            if (work is not null) await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }

    public async Task InterruptAsync(string reason, CancellationToken cancellationToken = default)
    {
        ReceiveSession? session;
        lock (_gate) { _sendInterruption = reason; session = _receive; }
        await CancelAsync(false, cancellationToken).ConfigureAwait(false);
        if (session is not null) await FinishReceiveAsync(session, NearbyTaskState.Failed, reason).ConfigureAwait(false);
    }
}
