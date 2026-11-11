using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using GlassDesk.Core.Contracts;

namespace GlassDesk.Nearby;

public sealed partial class NearbyTransferService
{
    private HttpClient CreateClient(string address, int port, string? pinned, Action<string>? captured = null)
    {
        if (!IPAddress.TryParse(address, out var ip) || !LocalNetwork.IsLocal(ip, AllowLoopback) || port is < 1 or > 65535)
            throw new ArgumentException("仅支持当前 IPv4 局域网内的 IP 地址和有效端口。");
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, ClientCertificateOptions = ClientCertificateOption.Manual };
        handler.ClientCertificates.Add(_identity ?? throw new InvalidOperationException("请先开启附近传输。"));
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null || !CertificateIdentity.Validate(certificate)) return false;
            var fingerprint = CertificateIdentity.Fingerprint(certificate);
            if (pinned is not null && !fingerprint.Equals(pinned, StringComparison.OrdinalIgnoreCase)) return false;
            captured?.Invoke(fingerprint);
            return true;
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri($"https://{ip}:{port}/api/localsend/v2/") };
    }

    public async Task<NearbyDevice> AddDeviceAsync(string address, int port = 53317, CancellationToken cancellationToken = default)
    {
        if (!State.Running) throw new InvalidOperationException("请先开启附近传输。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        string? actual = null;
        using var client = CreateClient(address, port, null, fp => actual = fp);
        using var response = await client.PostAsJsonAsync("register", Info(), NearbyJson.Options, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<DeviceInfo>(NearbyJson.Options, timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("对方未返回有效设备信息。");
        if (actual is null) throw new IOException("无法核实对方证书。");
        Remember(info with { Port = port, Protocol = "https" }, address, actual);
        lock (_gate) return _devices.TryGetValue(actual, out var device) ? device : throw new IOException("对方设备信息无效或指向本机。");
    }

    public Task SendAsync(NearbyDevice device, IReadOnlyList<NearbyFile> files,
        Func<CancellationToken, Task<string?>>? requestPin = null, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) throw new ArgumentException("请先添加文件。");
        if (files.Count > 10000 || files.Any(f => f.Size < 0) || files.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new ArgumentException("发送文件信息无效。");
        _ = files.Aggregate(0L, (sum, file) => checked(sum + file.Size));
        lock (_gate)
        {
            if (!State.Running) throw new InvalidOperationException("请先开启附近传输。");
            if (_sending is not null) throw new InvalidOperationException("已有发送任务正在运行。");
            _sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
            _sendInterruption = null;
            _sendWork = SendCoreAsync(device, files, requestPin, _sending);
            return _sendWork;
        }
    }

    private async Task SendCoreAsync(NearbyDevice device, IReadOnlyList<NearbyFile> files,
        Func<CancellationToken, Task<string?>>? requestPin, CancellationTokenSource cancellation)
    {
        // Start asynchronously so callers can store the task before cancellation/cleanup.
        await Task.Yield();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var id = Guid.NewGuid().ToString("N");
        string? sessionId = null;
        var watch = Stopwatch.StartNew();
        HttpClient? client = null;
        try
        {
            var total = files.Aggregate(0L, (sum, f) => checked(sum + f.Size));
            Update(s => s with { Sending = new(id, false, device.Name, NearbyTaskState.Waiting, "等待对方确认", 0, total, 0, null, [], null) });
            client = CreateClient(device.Address, device.Port, device.Fingerprint);
            var request = new PrepareRequest(Info(), files.ToDictionary(f => f.Id, f => new WireFile(f.Id, f.Name, f.Size, f.ContentType, f.Sha256), StringComparer.Ordinal));
            PrepareResponse? prepared = null;
            string? pin = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15));
                using var response = await client.PostAsJsonAsync("prepare-upload" + (pin is null ? "" : "?pin=" + Uri.EscapeDataString(pin)), request, NearbyJson.Options, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && requestPin is not null)
                {
                    pin = await requestPin(cancellation.Token).ConfigureAwait(false);
                    if (pin is null) throw new OperationCanceledException("已取消 PIN 输入。");
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Conflict) throw new IOException("对方忙碌，请稍后重试。");
                if (response.StatusCode == HttpStatusCode.Forbidden) throw new IOException("对方拒收，或接收确认已超时。");
                if (response.StatusCode == HttpStatusCode.NoContent) throw new IOException("对方未选择接收文件。");
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IOException("对方要求 PIN，请重新发送。");
                response.EnsureSuccessStatusCode();
                prepared = await response.Content.ReadFromJsonAsync<PrepareResponse>(NearbyJson.Options, timeout.Token).ConfigureAwait(false);
                break;
            }
            if (prepared?.Files is null || string.IsNullOrEmpty(prepared.SessionId)) throw new IOException("对方未返回有效会话，或 PIN 不正确。");
            sessionId = prepared.SessionId;
            watch.Restart();
            long transferred = 0;
            foreach (var file in files)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!prepared.Files.TryGetValue(file.Id, out var token)) continue;
                var before = transferred;
                using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                uploadTimeout.CancelAfter(options.InactivityTimeout);
                using var content = new ProgressFileContent(file, amount =>
                {
                    uploadTimeout.CancelAfter(options.InactivityTimeout);
                    Update(s => s with { Sending = s.Sending! with { State = NearbyTaskState.Transferring, CurrentFile = file.Name, Transferred = before + amount,
                        BytesPerSecond = (before + amount) / Math.Max(.01, watch.Elapsed.TotalSeconds) },
                    });
                }, () => uploadTimeout.CancelAfter(options.InactivityTimeout));
                using var message = new HttpRequestMessage(HttpMethod.Post, "upload?sessionId=" + Uri.EscapeDataString(sessionId)
                    + "&fileId=" + Uri.EscapeDataString(file.Id) + "&token=" + Uri.EscapeDataString(token)) { Content = content };
                // The content stream enforces inactivity during upload; response headers also need a bound.
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, uploadTimeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                completed.Add(file.Id); transferred = checked(transferred + file.Size);
            }
            var failed = files.Where(f => !completed.Contains(f.Id)).ToArray();
            Update(s => s with { Sending = s.Sending! with { State = failed.Length == 0 ? NearbyTaskState.Completed : NearbyTaskState.Failed,
                FailedFiles = failed, Error = failed.Length == 0 ? null : "部分文件未被对方接收。", BytesPerSecond = 0 } });
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or ArgumentException or OverflowException or System.Text.Json.JsonException)
        {
            var cancelled = _sendInterruption is null && (cancellation.IsCancellationRequested || error is OperationCanceledException && error.Message.Contains("PIN", StringComparison.Ordinal));
            Update(s => s with { Sending = s.Sending is null ? null : s.Sending with { State = cancelled ? NearbyTaskState.Cancelled : NearbyTaskState.Failed,
                Error = _sendInterruption ?? (cancelled ? "已取消发送。" : error is OperationCanceledException ? "等待确认或网络响应超时，请重试。"
                    : error is HttpRequestException ? "无法完成传输，请确认对方在线，并检查局域网和防火墙后重试。" : error.Message),
                FailedFiles = files.Where(f => !completed.Contains(f.Id)).ToArray(), BytesPerSecond = 0 } });
        }
        finally
        {
            if (client is not null && sessionId is not null && completed.Count != files.Count)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { using var response = await client.PostAsync("cancel?sessionId=" + Uri.EscapeDataString(sessionId), null, timeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
            client?.Dispose();
            lock (_gate) { if (ReferenceEquals(_sending, cancellation)) _sending = null; }
            cancellation.Dispose();
        }
    }
}
