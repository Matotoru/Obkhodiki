using Obkhodiki.Core.Tests.TestDoubles;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.Core.Tests.Updates;

public class AppReleaseTests
{
    private const string Digest = "244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058";
    private static readonly string Base = $"https://github.com/{AppInfo.Repository}/releases/download/v0.4.0/";

    private static string Release(string name, string url) => $$"""
        {
          "tag_name": "v0.4.0",
          "assets": [
            { "name": "Obkhodiki-0.4.0-win-x64.zip.sha256", "digest": "sha256:{{Digest}}", "browser_download_url": "{{Base}}x.sha256" },
            { "name": "{{name}}", "digest": "sha256:{{Digest}}", "browser_download_url": "{{url}}" }
          ]
        }
        """;

    [Fact]
    public async Task GetLatest_PicksTheWindowsZipOfTheAppRepository()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Release(AppInfo.AssetName("0.4.0"), Base + AppInfo.AssetName("0.4.0"))));

        var release = await new AppReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.4.0", release.Version);
        Assert.Equal(Digest, release.Sha256);
        Assert.Contains(AppInfo.Repository, handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetLatest_AssetFromAnotherRepository_Rejected()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Release(AppInfo.AssetName("0.4.0"),
            "https://github.com/Attacker/Obkhodiki/releases/download/v0.4.0/" + AppInfo.AssetName("0.4.0"))));

        await Assert.ThrowsAsync<UpdateException>(() => new AppReleaseClient(new HttpClient(handler)).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public void Changelog_NewestFirstAndValidVersions()
    {
        var versions = Changelog.Entries.Select(e => e.Version).ToList();
        Assert.All(versions, v => ReleaseVersion.Normalize(v));
        for (var i = 1; i < versions.Count; i++) Assert.True(ReleaseVersion.IsNewer(versions[i - 1], versions[i]));
        Assert.All(Changelog.Entries, e => Assert.NotEmpty(e.Items));
    }

    [Fact]
    public void Changelog_SinceShowsOnlyWhatIsNewUpToCurrent()
    {
        Assert.Equal(new[] { "0.4.0", "0.3.1" }, Changelog.Since("0.3.0", "0.4.0").Select(e => e.Version));
        Assert.Empty(Changelog.Since("0.4.0", "0.4.0"));
        // A newer entry than the running build (written ahead of a release) is not shown yet.
        Assert.Equal(new[] { "0.3.1" }, Changelog.Since("0.3.0", "0.3.1").Select(e => e.Version));
    }
}
