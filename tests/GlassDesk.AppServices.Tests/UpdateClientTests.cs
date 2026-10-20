using System.Net;
using System.Security.Cryptography;
using System.Text;
using GlassDesk.AppServices.Updates;

namespace GlassDesk.AppServices.Tests;

public sealed class UpdateClientTests
{
    private static readonly Uri Source = new("https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest");
    private static string Fixture(string version = "0.2.10", string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", long size = 4) => $$"""
        {"tag_name":"v{{version}}","draft":false,"prerelease":false,"body":"更新说明",
        "html_url":"https://github.com/everlosttime/glassdesk-updates/releases/tag/v{{version}}",
        "assets":[{"name":"GlassDesk-{{version}}-win-x64-Setup.exe","state":"uploaded","size":{{size}},"digest":"{{digest}}",
        "browser_download_url":"https://github.com/everlosttime/glassdesk-updates/releases/download/v{{version}}/GlassDesk-{{version}}-win-x64-Setup.exe"}]}
        """;

    [Fact]
    public void Selects_numeric_newer_version_and_matching_verified_asset()
    {
        var release = UpdateClient.ParseRelease(Fixture(), new Version(0, 2, 9), Source);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 10), release.Version);
        Assert.Equal("更新说明", release.Notes);
        Assert.Equal("GlassDesk-0.2.10-win-x64-Setup.exe", release.FileName);
    }

    [Theory]
    [InlineData("0.2.8")]
    [InlineData("0.2.9")]
    public void Never_offers_equal_or_older_versions(string version) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture(version), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("\"prerelease\":false", "\"prerelease\":true")]
    [InlineData("\"draft\":false", "\"draft\":true")]
    public void Ignores_unpublished_and_preview_releases(string before, string after) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture().Replace(before, after), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("0.2.10-preview")]
    [InlineData("0.2.10.1")]
    [InlineData("0.02.10")]
    [InlineData("999999999999.0.0")]
    public void Rejects_ambiguous_or_unsupported_versions(string version) =>
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(Fixture(version), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("https://github.com/everlosttime/glassdesk-updates/releases/download/", "https://evil.example/releases/download/")]
    [InlineData("https://github.com/everlosttime/glassdesk-updates/releases/download/", "http://github.com/everlosttime/glassdesk-updates/releases/download/")]
    [InlineData("\"state\":\"uploaded\"", "\"state\":\"new\"")]
    [InlineData("\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "\"digest\":null")]
    [InlineData("\"size\":4", "\"size\":0")]
    [InlineData("\"size\":4", "\"size\":536870913")]
    [InlineData("-win-x64-Setup.exe", "-win-arm64-Setup.exe")]
    public void Rejects_untrusted_or_incomplete_installers(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(Fixture().Replace(before, after), new Version(0, 2, 9), Source));

    [Fact]
    public async Task Download_writes_exact_bytes_and_reports_completion_only_after_validation()
    {
        var bytes = Encoding.UTF8.GetBytes("a real installer fixture");
        var release = Release(bytes);
        using var client = new HttpClient(new FixtureHandler(bytes));
        var updater = new UpdateClient(client, Source);
        var root = TempRoot();
        var percentages = new List<int>();
        try
        {
            var file = await updater.DownloadAsync(release, root, new InlineProgress(percentages), CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.EndsWith(release.FileName, file);
            Assert.Equal(100, percentages[^1]);
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("truncate")]
    [InlineData("oversize")]
    public async Task Corrupt_download_never_leaves_an_installable_or_partial_file(string mode)
    {
        var expected = Encoding.UTF8.GetBytes("expected installer");
        var actual = mode switch { "tamper" => Encoding.UTF8.GetBytes("modified installer"), "truncate" => expected[..3], _ => new byte[expected.Length + 1] };
        using var http = new HttpClient(new FixtureHandler(actual, omitLength: true));
        var updater = new UpdateClient(http, Source);
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(Release(expected), root, null, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_download()
    {
        var bytes = new byte[200000];
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FixtureHandler(bytes));
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateClient(http, Source).DownloadAsync(
                Release(bytes), root, new CancelProgress(cancellation), cancellation.Token));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Metadata_http_failure_is_not_reported_as_latest_version()
    {
        using var http = new HttpClient(new FixtureHandler([], HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(http, Source).CheckAsync(new Version(0, 2, 9), CancellationToken.None));
    }

    private static UpdateRelease Release(byte[] bytes) => UpdateClient.ParseRelease(
        Fixture(digest: "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), size: bytes.Length), new Version(0, 2, 9), Source)!;
    private static string TempRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "GlassDesk-update-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private sealed class InlineProgress(List<int> values) : IProgress<int> { public void Report(int value) => values.Add(value); }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<int> { public void Report(int value) { if (value > 0) source.Cancel(); } }
    private sealed class FixtureHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK, bool omitLength = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.UserAgent.Any());
            return Task.FromResult(new HttpResponseMessage(status) { Content = omitLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes) });
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
