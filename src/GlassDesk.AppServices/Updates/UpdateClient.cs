using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassDesk.AppServices.Updates;

/// <summary>Stable GitHub releases and bounded, checksum-verified installer downloads.</summary>
public sealed partial class UpdateClient
{
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    private const int MaximumMetadataSize = 512 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _source;

    public UpdateClient(HttpClient http, Uri source)
    {
        ArgumentNullException.ThrowIfNull(http);
        Repository(source);
        _http = http;
        _source = source;
    }

    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(_source, metadata: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumMetadataSize)
            throw new InvalidDataException("更新信息过大。");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumMetadataSize) throw new InvalidDataException("更新信息过大。");
            output.Write(buffer, 0, read);
        }
        return ParseRelease(System.Text.Encoding.UTF8.GetString(output.ToArray()), current, _source);
    }

    public static UpdateRelease? ParseRelease(string json, Version current, Uri source)
    {
        var repository = Repository(source);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var text = tag.StartsWith('v') ? tag[1..] : tag;
            if (!StableVersionPattern().IsMatch(text) || !Version.TryParse(text, out var version))
                throw new InvalidDataException("更新版本号无效。仅支持正式版本。");
            // Compare product version, not the intentionally stable assembly identity (0.2.2.0).
            var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (version <= installed) return null;
            var fileName = $"GlassDesk-{version}-win-x64-Setup.exe";
            var assets = root.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == fileName).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("此版本没有唯一的 Windows x64 安装包。");
            var asset = assets[0];
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerSize || asset.GetProperty("state").GetString() != "uploaded")
                throw new InvalidDataException("更新安装包尚未就绪或大小无效。");
            var digest = asset.GetProperty("digest").GetString() ?? string.Empty;
            if (!DigestPattern().IsMatch(digest)) throw new InvalidDataException("更新安装包缺少 SHA-256 校验信息。");
            var url = asset.GetProperty("browser_download_url").GetString();
            var expected = $"https://github.com/{repository}/releases/download/{tag}/{fileName}";
            var page = root.GetProperty("html_url").GetString();
            if (url != expected || page != $"https://github.com/{repository}/releases/tag/{tag}")
                throw new InvalidDataException("更新地址不属于配置的发布仓库。");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            return new UpdateRelease(version, fileName, new Uri(url), digest[7..].ToLowerInvariant(), size,
                notes.Length > 12000 ? notes[..12000] : notes, new Uri(page!));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("更新信息格式无效。", ex);
        }
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string cacheRoot, IProgress<int>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        var repository = Repository(_source);
        if (release.DownloadUrl.AbsoluteUri != $"https://github.com/{repository}/releases/download/v{release.Version}/{release.FileName}"
            && release.DownloadUrl.AbsoluteUri != $"https://github.com/{repository}/releases/download/{release.Version}/{release.FileName}")
            throw new InvalidDataException("更新下载地址无效。");
        ValidateInstaller(release);
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "installer.partial");
        var final = Path.Combine(directory, release.FileName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var request = Request(release.DownloadUrl, metadata: false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                throw new InvalidDataException("更新下载被重定向到不安全的地址。");
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("安装包大小与更新信息不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var lastProgress = -1;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                progress?.Report(0);
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("安装包超出声明大小。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    var percent = (int)Math.Min(99, received * 100 / release.Size);
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，请重试下载。");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, final);
            progress?.Report(100);
            return final;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void ValidateInstaller(UpdateRelease release)
    {
        if (!StableVersionPattern().IsMatch(release.Version.ToString())
            || release.FileName != $"GlassDesk-{release.Version}-win-x64-Setup.exe"
            || !DigestPattern().IsMatch("sha256:" + release.Sha256)
            || release.Size <= 0 || release.Size > MaximumInstallerSize)
            throw new InvalidDataException("安装包信息无效。");
    }

    private static HttpRequestMessage Request(Uri url, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("GlassDesk-Updater/1.0");
        if (metadata) request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static string Repository(Uri source)
    {
        if (!source.IsAbsoluteUri || source.Scheme != "https" || source.Host != "api.github.com"
            || !source.IsDefaultPort || source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0
            || !SourcePattern().IsMatch(source.AbsolutePath))
            throw new InvalidDataException("更新源必须是 GitHub 的公开仓库 Releases 接口。");
        return string.Join('/', source.AbsolutePath.Split('/').Skip(2).Take(2));
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();
    [GeneratedRegex(@"^sha256:[0-9a-fA-F]{64}$")]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"^/repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/latest$")]
    private static partial Regex SourcePattern();
}
