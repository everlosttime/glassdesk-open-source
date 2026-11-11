using System.Net;
using System.Text;
using GlassDesk.AppServices.Updates;

namespace GlassDesk.AppServices.Tests;

public sealed class UpdateNoticeTests
{
    private static readonly Uri Source = new("https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest");
    private static string Notice(string tag = "v1.4.4", string extra = "") =>
        $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"body":"更新说明"{{extra}}} """;

    [Theory]
    [InlineData("")]
    [InlineData(",\"assets\":[]")]
    [InlineData(",\"assets\":null")]
    public async Task New_release_is_detected_without_any_installer_or_download_information(string assets)
    {
        using var handler = new MetadataHandler(Notice(extra: assets));
        using var http = new HttpClient(handler);
        var notice = await new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None);
        Assert.NotNull(notice);
        Assert.Equal(new Version(1, 4, 4), notice.Version);
        Assert.Equal("更新说明", notice.Notes);
        Assert.Equal("https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Installer_assets_do_not_affect_version_discovery_or_trigger_an_asset_request()
    {
        const string assets = ",\"assets\":[{\"name\":\"unrelated.zip\",\"state\":\"new\",\"size\":-1,\"browser_download_url\":\"http://untrusted.invalid/setup.exe\"}]";
        using var handler = new MetadataHandler(Notice(extra: assets));
        using var http = new HttpClient(handler);
        var notice = await new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None);
        Assert.NotNull(notice);
        Assert.Equal(new Version(1, 4, 4), notice.Version);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("v1.4.3")]
    [InlineData("v1.4.2")]
    public async Task Equal_and_older_releases_do_not_offer_an_update(string tag)
    {
        using var http = new HttpClient(new MetadataHandler(Notice(tag)));
        Assert.Null(await new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None));
    }

    [Theory]
    [InlineData("v1.4.10")]
    [InlineData("1.4.10")]
    public async Task Product_versions_are_compared_numerically(string tag)
    {
        using var http = new HttpClient(new MetadataHandler(Notice(tag)));
        var notice = await new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 9, 0), CancellationToken.None);
        Assert.NotNull(notice);
        Assert.Equal(new Version(1, 4, 10), notice.Version);
    }

    [Theory]
    [InlineData("\"draft\":false", "\"draft\":true")]
    [InlineData("\"prerelease\":false", "\"prerelease\":true")]
    public async Task Unpublished_or_preview_versions_are_not_offered(string before, string after)
    {
        using var http = new HttpClient(new MetadataHandler(Notice().Replace(before, after)));
        Assert.Null(await new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None));
    }

    [Theory]
    [InlineData("v1.4.4-preview")]
    [InlineData("v1.04.4")]
    [InlineData("v1.4.4.1")]
    [InlineData("v999999999999.4.4")]
    public async Task Ambiguous_or_unsupported_versions_are_rejected(string tag)
    {
        using var http = new HttpClient(new MetadataHandler(Notice(tag)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v1.4.4\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v1.4.4\",\"draft\":false,\"prerelease\":false,\"body\":null}")]
    public async Task Release_without_notes_still_announces_the_version(string json)
    {
        using var http = new HttpClient(new MetadataHandler(json));
        var notice = await new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None);
        Assert.NotNull(notice);
        Assert.Empty(notice.Notes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"v1.4.4\",\"draft\":false,\"prerelease\":false,\"body\":42}")]
    public async Task Malformed_information_is_not_reported_as_no_update(string json)
    {
        using var http = new HttpClient(new MetadataHandler(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Api_failures_are_not_reported_as_no_update(HttpStatusCode status)
    {
        using var http = new HttpClient(new MetadataHandler("{}", status));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task Oversized_metadata_is_rejected_before_displaying_a_notice()
    {
        using var http = new HttpClient(new MetadataHandler(new string('x', 512 * 1024 + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http, Source).CheckAsync(new Version(1, 4, 3), CancellationToken.None));
    }

    private sealed class MetadataHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
