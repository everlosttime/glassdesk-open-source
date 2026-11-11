using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GlassDesk.Nearby.Tests;

public sealed class ProtocolTests : IAsyncLifetime
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ProtocolTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-Protocol-" + Guid.NewGuid().ToString("N"));
    private NearbyTransferService _sender = null!, _receiver = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _sender = NewService("send", 54170); _receiver = NewService("receive", 54190);
        await _sender.StartAsync(new NearbySettings { DeviceName = "发送方" });
        await _receiver.StartAsync(new NearbySettings { DeviceName = "接收方" });
    }
    private NearbyTransferService NewService(string name, int port) => new(new NearbyOptions
    {
        LoadIdentity = CreateCertificate, DefaultReceiveDirectory = Path.Combine(_root, name),
        BindAddress = IPAddress.Loopback, FirstPort = port, DiscoveryEnabled = false,
        DecisionTimeout = TimeSpan.FromSeconds(4),
    });
    internal static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend User", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    public async Task DisposeAsync()
    {
        await _sender.DisposeAsync(); await _receiver.DisposeAsync(); Directory.Delete(_root, true);
    }

    [Fact]
    public async Task TwoHttpsInstancesTransferOriginalUnicodeMediaAndRejectExtraReceive()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        var path = Path.Combine(_root, "原图😀.heic"); await File.WriteAllBytesAsync(path, bytes);
        var device = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        var pending = _sender.SendAsync(device, [new NearbyFile("a", "照片/原图😀.heic", bytes.Length, "image/heic", path)]);
        await WaitUntil(() => _receiver.State.Incoming is not null);
        await using var third = NewService("third", 54210); await third.StartAsync(new NearbySettings());
        var thirdDevice = await third.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        await third.SendAsync(thirdDevice, [new NearbyFile("b", "other.heic", bytes.Length, "image/heic", path)]);
        Assert.Equal(NearbyTaskState.Failed, third.State.Sending!.State);
        Assert.Contains("忙碌", third.State.Sending.Error);
        await _receiver.DecideAsync(_receiver.State.Incoming!.Id, true);
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(NearbyTaskState.Completed, _sender.State.Sending!.State);
        Assert.Equal(NearbyTaskState.Completed, _receiver.State.Receiving!.State);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_root, "receive", "照片", "原图😀.heic"))));
    }

    [Fact]
    public async Task RejectAndCancelWhileWaitingReleaseReceiveSlot()
    {
        var path = Path.Combine(_root, "x"); File.WriteAllText(path, "abc");
        var device = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        var files = new[] { new NearbyFile("a", "a.jpg", 3, "image/jpeg", path) };
        var first = _sender.SendAsync(device, files);
        await WaitUntil(() => _receiver.State.Incoming is not null);
        await _receiver.DecideAsync(_receiver.State.Incoming!.Id, false); await first;
        Assert.Equal(NearbyTaskState.Failed, _sender.State.Sending!.State);
        var second = _sender.SendAsync(device, files);
        await WaitUntil(() => _receiver.State.Incoming is not null);
        await _sender.CancelAsync(false); await second;
        await WaitUntil(() => _receiver.State.Incoming is null);
        Assert.Equal(NearbyTaskState.Cancelled, _sender.State.Sending!.State);
    }

    [Fact]
    public async Task DisabledServiceNeverLoadsIdentityAndStopReleasesTcpPort()
    {
        var loads = 0;
        await using var service = new NearbyTransferService(new NearbyOptions
        {
            LoadIdentity = () => { loads++; return CreateCertificate(); }, DefaultReceiveDirectory = _root,
            BindAddress = IPAddress.Loopback, FirstPort = 54240, DiscoveryEnabled = false,
        });
        Assert.Equal(0, loads); Assert.False(service.State.Running);
        await service.StartAsync(new NearbySettings()); Assert.Equal(1, loads);
        var port = service.State.Port;
        await service.StopAsync();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        listener.Start(); listener.Stop();
        Assert.False(service.State.Running);
    }

    [Fact]
    public async Task ReceiveDirectoryCannotBeInsideApplicationData()
    {
        var loads = 0;
        await using var service = new NearbyTransferService(new NearbyOptions
        {
            LoadIdentity = () => { loads++; return CreateCertificate(); }, DefaultReceiveDirectory = _root,
            ForbiddenReceiveRoots = [_root], BindAddress = IPAddress.Loopback, FirstPort = 54240, DiscoveryEnabled = false,
        });
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(new NearbySettings { ReceiveDirectory = Path.Combine(_root, "Data", "received") }));
        Assert.Equal(0, loads); Assert.False(service.State.Running);
    }

    [Fact]
    public async Task SystemInterruptionFailsActiveTaskWithoutDisablingListener()
    {
        var path = Path.Combine(_root, "x"); File.WriteAllText(path, "abc");
        var device = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        var sending = _sender.SendAsync(device, [new NearbyFile("a", "a.png", 3, "image/png", path)]);
        await WaitUntil(() => _receiver.State.Incoming is not null);
        await _sender.InterruptAsync("系统进入睡眠，连接已中断，请重试。"); await sending;
        Assert.Equal(NearbyTaskState.Failed, _sender.State.Sending!.State);
        Assert.Contains("睡眠", _sender.State.Sending.Error);
        Assert.True(_sender.State.Running);
    }

    [Fact]
    public async Task RepeatedRegistrationDoesNotRefreshTheDeviceList()
    {
        var changes = 0; _sender.Changed += (_, _) => changes++;
        await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        var first = changes;
        await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        Assert.Equal(first, changes);
        Assert.Single(_sender.State.Devices);
    }

    [Fact]
    public async Task ConfirmationTimeoutRejectsAndReleasesTheSlot()
    {
        var path = Path.Combine(_root, "x"); File.WriteAllText(path, "abc");
        var device = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        await _sender.SendAsync(device, [new NearbyFile("a", "a.png", 3, "image/png", path)]);
        Assert.Equal(NearbyTaskState.Failed, _sender.State.Sending!.State);
        Assert.Null(_receiver.State.Incoming);
        Assert.True(_receiver.State.Running);
        Assert.Equal(NearbyTaskState.Cancelled, _receiver.State.Receiving!.State);
    }

    [Fact]
    public async Task PinChallengeRetriesPreparationAndSendsOriginalMimeAndBytes()
    {
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        await using var server = builder.Build();
        byte[]? uploaded = null; string? mime = null;
        server.MapPost("/api/localsend/v2/prepare-upload", (Func<HttpContext, IResult>)(context =>
            context.Request.Query["pin"] == "123456" ? Results.Json(new { sessionId = "pin-session", files = new Dictionary<string, string> { ["a"] = "pin-file-token" } }) : Results.Unauthorized()));
        server.MapPost("/api/localsend/v2/upload", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            if (context.Request.Query["sessionId"] != "pin-session" || context.Request.Query["token"] != "pin-file-token") return Results.StatusCode(403);
            using var buffer = new MemoryStream(); await context.Request.Body.CopyToAsync(buffer);
            uploaded = buffer.ToArray(); mime = context.Request.ContentType; return Results.Ok();
        }));
        await server.StartAsync();
        var port = new Uri(server.Urls.Single()).Port;
        var path = Path.Combine(_root, "pin.png"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var device = new NearbyDevice(CertificateIdentity.Fingerprint(certificate), "PIN device", "127.0.0.1", port);
        var prompts = 0;
        await _sender.SendAsync(device, [new NearbyFile("a", "pin.png", 3, "image/png", path)], _ => { prompts++; return Task.FromResult<string?>("123456"); });
        Assert.Equal(NearbyTaskState.Completed, _sender.State.Sending!.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, uploaded); Assert.Equal("image/png", mime); Assert.Equal(1, prompts);
        await server.StopAsync();
    }

    [Fact]
    public async Task MismatchedServerCertificateCannotStartAReceiveRequest()
    {
        var device = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        var path = Path.Combine(_root, "x"); File.WriteAllText(path, "abc");
        await _sender.SendAsync(device with { Fingerprint = new string('0', 64) }, [new NearbyFile("a", "a.png", 3, "image/png", path)]);
        Assert.Equal(NearbyTaskState.Failed, _sender.State.Sending!.State);
        Assert.Null(_receiver.State.Incoming);
        Assert.False(Directory.Exists(Path.Combine(_root, "receive")));
    }

    [Fact]
    public async Task UploadWithoutAnyResponseProgressTimesOutWithoutATotalDurationLimit()
    {
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        await using var server = builder.Build();
        server.MapPost("/api/localsend/v2/prepare-upload", () => Results.Json(new { sessionId = "idle", files = new Dictionary<string, string> { ["a"] = "idle-token" } }));
        server.MapPost("/api/localsend/v2/upload", (Func<HttpContext, Task>)(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); } catch (OperationCanceledException) { }
        }));
        await server.StartAsync();
        await using var sender = new NearbyTransferService(new NearbyOptions { LoadIdentity = CreateCertificate,
            DefaultReceiveDirectory = _root, BindAddress = IPAddress.Loopback, FirstPort = 54360, DiscoveryEnabled = false,
            InactivityTimeout = TimeSpan.FromMilliseconds(300) });
        await sender.StartAsync(new NearbySettings());
        var path = Path.Combine(_root, "idle.png"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var device = new NearbyDevice(CertificateIdentity.Fingerprint(certificate), "idle", "127.0.0.1", new Uri(server.Urls.Single()).Port);
        await sender.SendAsync(device, [new NearbyFile("a", "idle.png", 3, "image/png", path)]).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NearbyTaskState.Failed, sender.State.Sending!.State);
        Assert.Contains("超时", sender.State.Sending.Error);
        await server.StopAsync();
    }

    [Fact]
    public async Task StopDuringUploadRemovesPartialAndReleasesDirectoryHandles()
    {
        using var certificate = CreateCertificate(); using var handler = new HttpClientHandler { UseProxy = false };
        handler.ClientCertificates.Add(certificate);
        handler.ServerCertificateCustomValidationCallback = (_, server, _, _) => server is not null && CertificateIdentity.Validate(server);
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_receiver.State.Port}/api/localsend/v2/") };
        var prepare = client.PostAsJsonAsync("prepare-upload", new { info = new { alias = "slow", version = "2.2", fingerprint = "ignored", protocol = "https", port = 53317 },
            files = new Dictionary<string, object> { ["a"] = new { id = "a", fileName = "partial.mov", size = 1024L, fileType = "video/quicktime" } } });
        await WaitUntil(() => _receiver.State.Incoming is not null); await _receiver.DecideAsync(_receiver.State.Incoming!.Id, true);
        using var prepared = await prepare; using var json = System.Text.Json.JsonDocument.Parse(await prepared.Content.ReadAsStringAsync());
        var session = json.RootElement.GetProperty("sessionId").GetString(); var token = json.RootElement.GetProperty("files").GetProperty("a").GetString();
        using var tcp = new System.Net.Sockets.TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, _receiver.State.Port);
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false);
        // The HTTP preparation already verifies the receiver. This connection pins the same server fingerprint below.
        var receiverFingerprint = (await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port)).Fingerprint;
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = "localhost", ClientCertificates = new X509CertificateCollection { certificate },
            RemoteCertificateValidationCallback = (_, server, _, _) => server is X509Certificate2 actual && CertificateIdentity.Fingerprint(actual) == receiverFingerprint,
        });
        var request = $"POST /api/localsend/v2/upload?sessionId={session}&fileId=a&token={token} HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1024\r\nConnection: close\r\n\r\nabc";
        await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request)); await tls.FlushAsync();
        await WaitUntil(() => _receiver.State.Receiving?.State == NearbyTaskState.Transferring);
        await _receiver.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "receive"), "*", SearchOption.AllDirectories));
        var moved = Path.Combine(_root, "released"); Directory.Move(Path.Combine(_root, "receive"), moved); Directory.Delete(moved);
        Assert.False(_receiver.State.Running);
    }

    [Fact]
    public async Task HundredImagesAndConcurrentReverseSendKeepBytesAndDirectoryStructure()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQ0AAAAASUVORK5CYII=");
        var path = Path.Combine(_root, "fixture.png"); await File.WriteAllBytesAsync(path, png);
        var files = Enumerable.Range(0, 100).Select(i => new NearbyFile(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"相册/第{i}张😀.png", png.Length, "image/png", path)).ToArray();
        _receiver.Changed += (_, _) => { if (_receiver.State.Incoming is { } incoming) _ = _receiver.DecideAsync(incoming.Id, true); };
        _sender.Changed += (_, _) => { if (_sender.State.Incoming is { } incoming) _ = _sender.DecideAsync(incoming.Id, true); };
        var forward = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        var reverse = await _receiver.AddDeviceAsync("127.0.0.1", _sender.State.Port);
        await Task.WhenAll(_sender.SendAsync(forward, files), _receiver.SendAsync(reverse, [new NearbyFile("back", "返回.png", png.Length, "image/png", path)]));
        Assert.Equal(NearbyTaskState.Completed, _sender.State.Sending!.State);
        Assert.Equal(NearbyTaskState.Completed, _receiver.State.Sending!.State);
        var received = Directory.GetFiles(Path.Combine(_root, "receive", "相册"));
        Assert.Equal(100, received.Length);
        foreach (var file in received) Assert.Equal(png, await File.ReadAllBytesAsync(file));
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(_root, "send", "返回.png")));
    }

    [LargeTransferFact]
    [Trait("Category", "LargeTransfer")]
    public async Task StreamLargerThanFourGiBHasBoundedMemoryAndMatchingHashes()
    {
        const long length = 4L * 1024 * 1024 * 1024 + 123;
        var path = Path.Combine(_root, "large-stream.bin");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.SetLength(length);
        _receiver.Changed += (_, _) => { if (_receiver.State.Incoming is { } incoming) _ = _receiver.DecideAsync(incoming.Id, true); };
        var device = await _sender.AddDeviceAsync("127.0.0.1", _receiver.State.Port);
        using var process = System.Diagnostics.Process.GetCurrentProcess(); process.Refresh();
        var baseline = process.WorkingSet64; var peak = baseline;
        using var monitor = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            try { while (!monitor.IsCancellationRequested) { process.Refresh(); peak = Math.Max(peak, process.WorkingSet64); await Task.Delay(100, monitor.Token); } }
            catch (OperationCanceledException) { }
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _sender.SendAsync(device, [new NearbyFile("big", "large-stream.bin", length, "application/octet-stream", path)]);
        monitor.Cancel(); await sampling;
        Assert.Equal(NearbyTaskState.Completed, _sender.State.Sending!.State);
        Assert.Equal(length, new FileInfo(Path.Combine(_root, "receive", "large-stream.bin")).Length);
        await using var original = File.OpenRead(path); await using var received = File.OpenRead(Path.Combine(_root, "receive", "large-stream.bin"));
        var expected = await SHA256.HashDataAsync(original); var actual = await SHA256.HashDataAsync(received);
        Assert.Equal(expected, actual);
        _output.WriteLine($"Bytes={length}; elapsed={watch.Elapsed}; baselineWorkingSet={baseline}; peakWorkingSet={peak}; sha256={Convert.ToHexString(actual)}");
        Assert.True(peak - baseline < 256L * 1024 * 1024, $"Memory growth {peak - baseline} exceeded 256MiB for a 4GiB stream.");
    }

    private sealed class LargeTransferFactAttribute : FactAttribute
    {
        public LargeTransferFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("GLASSDESK_NEARBY_LARGE_TEST") != "1") Skip = "Set GLASSDESK_NEARBY_LARGE_TEST=1 to write and transfer a 4GiB+123-byte test stream.";
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongTokenAndWrongCertificateCannotWriteApprovedFile(bool chunked)
    {
        using var cert = CreateCertificate();
        using var handler = new HttpClientHandler { UseProxy = false };
        handler.ClientCertificates.Add(cert);
        handler.ServerCertificateCustomValidationCallback = (_, server, _, _) => server is not null && CertificateIdentity.Validate(server);
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_receiver.State.Port}/api/localsend/v2/") };
        var manifest = new { info = new { alias = "test", version = "2.2", fingerprint = "forged", port = 53317, protocol = "https" },
            files = new Dictionary<string, object> { ["a"] = new { id = "a", fileName = "a.png", size = 3L, fileType = "image/png" } } };
        var prepare = client.PostAsJsonAsync("prepare-upload", manifest);
        await WaitUntil(() => _receiver.State.Incoming is not null);
        await _receiver.DecideAsync(_receiver.State.Incoming!.Id, true);
        using var response = await prepare;
        // The untrusted advertised fingerprint is ignored in favour of the actual TLS certificate.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = json.RootElement.GetProperty("sessionId").GetString();
        var token = json.RootElement.GetProperty("files").GetProperty("a").GetString();
        using var wrong = await client.PostAsync($"upload?sessionId={session}&fileId=a&token=bad", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        using var otherCert = CreateCertificate(); using var otherHandler = new HttpClientHandler { UseProxy = false };
        otherHandler.ClientCertificates.Add(otherCert);
        otherHandler.ServerCertificateCustomValidationCallback = handler.ServerCertificateCustomValidationCallback;
        using var other = new HttpClient(otherHandler) { BaseAddress = client.BaseAddress };
        using var impostor = await other.PostAsync($"upload?sessionId={session}&fileId=a&token={token}", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Forbidden, impostor.StatusCode);
        using HttpContent content = chunked ? new UnknownLengthContent() : new ByteArrayContent([1, 2, 3]);
        using var good = await client.PostAsync($"upload?sessionId={session}&fileId=a&token={token}", content);
        Assert.True(good.IsSuccessStatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_root, "receive", "a.png")));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }
}
