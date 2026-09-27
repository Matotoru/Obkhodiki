using System.Net;
using Obkhodiki.Core.Tests.TestDoubles;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.Core.Tests.Updates;

public class FlowsealReleaseClientTests
{
    private const string AssetUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip";
    private const string Digest = "244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058";

    private static string Release(string zipUrl = AssetUrl, string? digest = "sha256:" + Digest, string zipName = "zapret-discord-youtube-1.10.3.zip")
    {
        var digestField = digest is null ? "" : $"\"digest\": \"{digest}\",";
        return $$"""
        {
          "tag_name": "1.10.3",
          "assets": [
            { "name": "zapret-discord-youtube-1.10.3.rar", "digest": "sha256:{{Digest}}", "browser_download_url": "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/a.rar" },
            { "name": "{{zipName}}", {{digestField}} "browser_download_url": "{{zipUrl}}" }
          ]
        }
        """;
    }

    private static readonly string LatestJson = Release();

    [Fact]
    public async Task GetLatest_PicksZipAssetWithUrlAndDigest()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(LatestJson));
        var client = new FlowsealReleaseClient(new HttpClient(handler));

        var release = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal("1.10.3", release.Version);
        Assert.Equal(new Uri(AssetUrl), release.AssetUrl);
        Assert.Equal(Digest, release.Sha256);
        Assert.Equal("api.github.com", handler.Requests.Single().RequestUri!.Host);
        Assert.Contains("Flowseal/zapret-discord-youtube", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("https://evil.test/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip")]
    [InlineData("http://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip")]
    [InlineData("https://github.com/Attacker/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip")]
    public async Task GetLatest_AssetOutsideOfficialRepo_Rejected(string url)
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Release(zipUrl: url)));

        await Assert.ThrowsAsync<UpdateException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("md5:abc")]
    [InlineData("sha256:1234")]
    public async Task GetLatest_MissingOrInvalidDigest_Rejected(string? digest)
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Release(digest: digest)));

        await Assert.ThrowsAsync<UpdateException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_UnexpectedZipName_NotPicked()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Release(zipName: "payload.zip")));

        await Assert.ThrowsAsync<UpdateException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Download_MatchingDigest_ReturnsBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var release = new ReleaseInfo("1.0", new Uri(AssetUrl), Sha(bytes));
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        await using var stream = await new FlowsealReleaseClient(new HttpClient(handler)).DownloadAsync(release, CancellationToken.None);

        Assert.Equal(bytes, ((MemoryStream)stream).ToArray());
    }

    [Fact]
    public async Task Download_DigestMismatch_Rejected()
    {
        var release = new ReleaseInfo("1.0", new Uri(AssetUrl), Sha(new byte[] { 9 }));
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });

        await Assert.ThrowsAsync<UpdateException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).DownloadAsync(release, CancellationToken.None));
    }

    [Fact]
    public async Task Download_Oversized_RejectedWithoutReadingEverything()
    {
        var release = new ReleaseInfo("1.0", new Uri(AssetUrl), Digest);
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentLength = FlowsealReleaseClient.MaxDownloadBytes + 1;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        await Assert.ThrowsAsync<UpdateException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).DownloadAsync(release, CancellationToken.None));
    }

    internal static string Sha(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task GetLatest_VPrefixedTag_IsNormalized()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(LatestJson.Replace("\"1.10.3\"", "\"v1.10.4\"")));

        var release = await new FlowsealReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None);

        Assert.Equal("1.10.4", release.Version);
    }

    [Fact]
    public async Task GetLatest_GarbageTag_ThrowsUpdateException()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(LatestJson.Replace("\"1.10.3\"", "\"latest\"")));

        await Assert.ThrowsAsync<UpdateException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_CallerCancellation_PropagatesAsCancellation()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(LatestJson));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FlowsealReleaseClient(new HttpClient(handler)).GetLatestAsync(cts.Token));
    }

    [Fact]
    public async Task GetLatest_SendsUserAgentRequiredByGitHub()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(LatestJson));
        var client = new FlowsealReleaseClient(new HttpClient(handler));

        await client.GetLatestAsync(CancellationToken.None);

        Assert.NotEmpty(handler.Requests.Single().Headers.UserAgent);
    }

    [Fact]
    public async Task GetLatest_NoZipAsset_ThrowsUpdateException()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{ "tag_name": "1.0", "assets": [] }"""));
        var client = new FlowsealReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_HttpError_ThrowsUpdateException()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new FlowsealReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_MalformedJson_ThrowsUpdateException()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("<html>rate limited</html>"));
        var client = new FlowsealReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_NetworkFailure_ThrowsUpdateException()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("reset"));
        var client = new FlowsealReleaseClient(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync(CancellationToken.None));
    }
}
