using System.Net;
using System.Runtime.InteropServices;
using Obkhodiki.Core.Telegram;
using Obkhodiki.Core.Tests.TestDoubles;
using Obkhodiki.Core.Tests.Updates;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.Core.Tests.Telegram;

public sealed class TgProxyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zh-tg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // A fake PE: only the "MZ" header and a minimum size are checked.
    private static byte[] Exe(byte fill = 0) => new byte[] { (byte)'M', (byte)'Z' }.Concat(Enumerable.Repeat(fill, 200)).ToArray();

    private static string ReleaseJson(string tag, string sha, string repo = "Flowseal/tg-ws-proxy") => $$"""
    { "tag_name": "{{tag}}", "assets": [
      { "name": "TgWsProxy_windows_7_64bit.exe", "digest": "sha256:{{sha}}", "browser_download_url": "https://github.com/{{repo}}/releases/download/{{tag}}/TgWsProxy_windows_7_64bit.exe" },
      { "name": "TgWsProxy_windows_arm64.exe", "digest": "sha256:{{sha}}", "browser_download_url": "https://github.com/{{repo}}/releases/download/{{tag}}/TgWsProxy_windows_arm64.exe" },
      { "name": "TgWsProxy_windows.exe", "digest": "sha256:{{sha}}", "browser_download_url": "https://github.com/{{repo}}/releases/download/{{tag}}/TgWsProxy_windows.exe" }
    ] }
    """;

    private static StubHttpHandler Handler(string tag, byte[] exe, byte[]? digestOf = null, string repo = "Flowseal/tg-ws-proxy") =>
        new(req => req.RequestUri!.Host == "api.github.com"
            ? StubHttpHandler.Json(ReleaseJson(tag, FlowsealReleaseClientTests.Sha(digestOf ?? exe), repo))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) });

    private (TgProxyUpdater Updater, TgProxyStore Store) Create(StubHttpHandler handler)
    {
        var store = new TgProxyStore(_root);
        return (new TgProxyUpdater(new TgProxyReleaseClient(new HttpClient(handler), Architecture.X64), store), store);
    }

    [Theory]
    [InlineData(Architecture.X64, "TgWsProxy_windows.exe")]
    [InlineData(Architecture.Arm64, "TgWsProxy_windows_arm64.exe")]
    public async Task ReleaseClient_PicksAssetForArchitectureAndNormalizesTag(Architecture arch, string asset)
    {
        var client = new TgProxyReleaseClient(new HttpClient(Handler("v1.10.4", Exe())), arch);

        var release = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal("1.10.4", release.Version);
        Assert.EndsWith("/" + asset, release.AssetUrl.AbsolutePath);
    }

    [Fact]
    public async Task ReleaseClient_AssetFromAnotherRepo_Rejected()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(ReleaseJson("v1.0", new string('0', 64), repo: "Evil/tg-ws-proxy")));

        await Assert.ThrowsAsync<UpdateException>(() =>
            new TgProxyReleaseClient(new HttpClient(handler), Architecture.X64).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public void Store_Install_ActivatesAndKeepsPreviousUntilCleanup()
    {
        var store = new TgProxyStore(_root);
        store.Install(new MemoryStream(Exe(1)), "1.10.3");
        var exe = store.Install(new MemoryStream(Exe(2)), "v1.10.4");

        Assert.Equal("1.10.4", store.ActiveVersion);
        Assert.Equal(exe, store.ActiveExe);
        Assert.True(File.Exists(store.ExePath("1.10.3")));

        store.CleanupInactive();

        Assert.False(File.Exists(store.ExePath("1.10.3")));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void Store_NotAnExecutable_RejectedAndPreviousStaysActive()
    {
        var store = new TgProxyStore(_root);
        store.Install(new MemoryStream(Exe()), "1.10.3");

        Assert.Throws<InvalidDataException>(() => store.Install(new MemoryStream(new byte[500]), "1.10.4"));

        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.10.4")));
    }

    [Theory]
    [InlineData(@"..\..\x")]
    [InlineData("garbage")]
    [InlineData("9.9.9")]
    public void Store_TamperedOrDanglingPointer_NoActive(string pointer)
    {
        var store = new TgProxyStore(_root);
        store.Install(new MemoryStream(Exe()), "1.10.3");
        File.WriteAllText(Path.Combine(_root, "active.txt"), pointer);

        Assert.Null(store.ActiveVersion);
        Assert.Null(store.ActiveExe);
    }

    [Fact]
    public async Task Updater_Check_NewerOnlyAndHonoursSkip()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe()));
        store.Install(new MemoryStream(Exe()), "1.10.3");

        Assert.Equal("1.10.4", (await updater.CheckAsync(null, CancellationToken.None))?.Version);
        Assert.Null(await updater.CheckAsync("1.10.4", CancellationToken.None));
    }

    [Fact]
    public async Task Updater_Install_SwitchesAndCleansUp()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe(7)));
        store.Install(new MemoryStream(Exe()), "1.10.3");
        var release = await updater.CheckAsync(null, CancellationToken.None);
        string? switchedTo = null;

        var installed = await updater.InstallAsync(release!, exe => { switchedTo = exe; return Task.CompletedTask; }, null, CancellationToken.None);

        Assert.Equal("1.10.4", installed);
        Assert.Equal(store.ExePath("1.10.4"), switchedTo);
        Assert.False(File.Exists(store.ExePath("1.10.3")));
    }

    [Fact]
    public async Task Updater_NewVersionFailsToStart_RollsBackAsReleaseDefect()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe(7)));
        store.Install(new MemoryStream(Exe()), "1.10.3");
        var release = await updater.CheckAsync(null, CancellationToken.None);
        string? rolledBackTo = null;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.InstallAsync(release!,
            _ => throw new InvalidOperationException("exited at once"),
            exe => { rolledBackTo = exe; return Task.CompletedTask; },
            CancellationToken.None));

        Assert.True(ex.ReleaseDefect);
        Assert.Equal("1.10.4", ex.Version);
        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.Equal(store.ExePath("1.10.3"), rolledBackTo);
    }

    [Fact]
    public async Task Updater_RollbackAlsoFails_StillDefectOldVersionActive()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe(7)));
        store.Install(new MemoryStream(Exe()), "1.10.3");
        var release = await updater.CheckAsync(null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.InstallAsync(release!,
            _ => throw new InvalidOperationException("new broken"),
            _ => throw new InvalidOperationException("old broken too"),
            CancellationToken.None));

        Assert.True(ex.ReleaseDefect);
        Assert.Contains("old broken too", ex.Message);
        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Fact]
    public async Task Updater_TargetLocked_UpdateExceptionNotDefect()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe(7)));
        store.Install(new MemoryStream(Exe()), "1.10.3");
        // A leftover folder for the new version holding a locked file: the store cannot replace it.
        var target = Path.Combine(_root, "versions", "1.10.4");
        Directory.CreateDirectory(target);
        using var _ = new FileStream(Path.Combine(target, "locked.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var release = await updater.CheckAsync(null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.InstallAsync(release!, null, null, CancellationToken.None));

        Assert.False(ex.ReleaseDefect);
        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Theory]
    [InlineData(null, "127.0.0.1:1443")]
    [InlineData("""{ "host": "0.0.0.0", "port": 2000 }""", "127.0.0.1:2000")]
    [InlineData("""{ "host": "::1%evil" }""", "127.0.0.1:1443")]
    public void Config_Endpoint_DefaultsForMissingOrSuspiciousConfig(string? json, string expected)
    {
        Assert.Equal(expected, TgProxyConfig.Endpoint(json).ToString());
    }

    [Fact]
    public async Task Updater_ChecksumMismatch_ReleaseDefectNothingInstalled()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe(7), digestOf: new byte[] { 1 }));
        store.Install(new MemoryStream(Exe()), "1.10.3");
        var release = await updater.CheckAsync(null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.InstallAsync(release!, null, null, CancellationToken.None));

        Assert.True(ex.ReleaseDefect);
        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Fact]
    public async Task Updater_SameOrOlderVersion_NoInstall()
    {
        var (updater, store) = Create(new StubHttpHandler(_ => throw new Xunit.Sdk.XunitException("must not download")));
        store.Install(new MemoryStream(Exe()), "1.10.4");

        var result = await updater.InstallAsync(
            new ReleaseInfo("1.10.4", new Uri("https://github.com/Flowseal/tg-ws-proxy/releases/download/v1.10.4/TgWsProxy_windows.exe"), new string('0', 64)),
            null, null, CancellationToken.None);

        Assert.Null(result);
    }

    // Nothing to roll back to: a first install that cannot start must not stay active.
    [Fact]
    public async Task Updater_FirstInstallFailsToStart_NothingLeftActive()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe()));
        var release = await updater.CheckAsync(null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            updater.InstallAsync(release!, _ => throw new InvalidOperationException("broken"), null, CancellationToken.None));

        Assert.True(ex.ReleaseDefect);
        Assert.Null(store.ActiveVersion);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.10.4")));
    }

    [Fact]
    public async Task Updater_FirstInstall_WithoutPreviousVersion()
    {
        var (updater, store) = Create(Handler("v1.10.4", Exe()));

        var release = await updater.CheckAsync(null, CancellationToken.None);
        await updater.InstallAsync(release!, null, null, CancellationToken.None);

        Assert.Equal("1.10.4", store.ActiveVersion);
    }

    [Theory]
    [InlineData("""{ "host": "127.0.0.1", "port": 1443, "secret": "0123456789abcdef0123456789ABCDEF" }""",
        "tg://proxy?server=127.0.0.1&port=1443&secret=dd0123456789abcdef0123456789abcdef")]
    [InlineData("""{ "host": "0.0.0.0", "port": 2000, "secret": "0123456789abcdef0123456789abcdef" }""",
        "tg://proxy?server=127.0.0.1&port=2000&secret=dd0123456789abcdef0123456789abcdef")]
    [InlineData("""{ "secret": "0123456789abcdef0123456789abcdef" }""",
        "tg://proxy?server=127.0.0.1&port=1443&secret=dd0123456789abcdef0123456789abcdef")]
    public void Config_BuildLink_Valid(string json, string expected)
    {
        Assert.Equal(expected, TgProxyConfig.BuildLink(json));
    }

    // The link is handed to Telegram: nothing from the file may add extra parameters or point elsewhere.
    [Theory]
    [InlineData("""{ "port": 1443 }""")]
    [InlineData("""{ "secret": "0123&server=evil" }""")]
    [InlineData("""{ "host": "127.0.0.1&server=evil.example", "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("""{ "port": 70000, "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("not json")]
    // .NET accepts any text as an IPv6 scope id; none of it may reach the link.
    [InlineData("""{ "host": "::1%z&server=evil.example.com&port=443&secret=eeAA#", "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("""{ "host": "::1%x,C:\\evil.exe", "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("""{ "host": "::1", "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("""{ "host": "1.2.3", "secret": "0123456789abcdef0123456789abcdef" }""")]
    // Wrong JSON types must not throw.
    [InlineData("""{ "secret": 123 }""")]
    [InlineData("""{ "host": 5, "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("""{ "port": "1443", "secret": "0123456789abcdef0123456789abcdef" }""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    // "$" in .NET regex also matches before a trailing newline; the checks must not.
    [InlineData("""{ "secret": "0123456789abcdef0123456789abcdef\n" }""")]
    [InlineData("""{ "host": "127.0.0.1\n", "secret": "0123456789abcdef0123456789abcdef" }""")]
    public void Config_BuildLink_MissingOrSuspicious_Null(string json)
    {
        Assert.Null(TgProxyConfig.BuildLink(json));
    }
}
