using System.Net;
using ZapretHub.Core.Tests.TestDoubles;
using ZapretHub.Core.Updates;

namespace ZapretHub.Core.Tests.Updates;

public sealed class EngineUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zh-upd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static byte[] ZipBytes(Dictionary<string, string>? files = null) =>
        EngineStoreTests.Zip(files ?? EngineStoreTests.ValidRelease()).ToArray();

    private static string LatestJson(string version, string sha256) => $$"""
    { "tag_name": "{{version}}", "assets": [ {
        "name": "zapret-discord-youtube-{{version}}.zip",
        "digest": "sha256:{{sha256}}",
        "browser_download_url": "https://github.com/Flowseal/zapret-discord-youtube/releases/download/{{version}}/zapret-discord-youtube-{{version}}.zip" } ] }
    """;

    // The advertised digest always matches the served bytes unless a test says otherwise.
    private static StubHttpHandler Handler(string version, Func<HttpResponseMessage> zipResponse, byte[]? digestOf = null) =>
        new(req => req.RequestUri!.Host == "api.github.com"
            ? StubHttpHandler.Json(LatestJson(version, FlowsealReleaseClientTests.Sha(digestOf ?? ZipBytes())))
            : zipResponse());

    private static HttpResponseMessage ZipOk(Dictionary<string, string>? files = null) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipBytes(files)) };

    private (EngineUpdater Updater, EngineStore Store) Create(StubHttpHandler handler)
    {
        var store = new EngineStore(_root);
        return (new EngineUpdater(new FlowsealReleaseClient(new HttpClient(handler)), store), store);
    }

    [Fact]
    public async Task Update_NewerRelease_InstalledActivatedCallbackRunOldCleaned()
    {
        var (updater, store) = Create(Handler("1.10.3", () => ZipOk()));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.2");
        string? callbackSaw = null;

        var result = await updater.UpdateAsync(l => { callbackSaw = l.Version; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.Equal("1.10.3", callbackSaw);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.10.2")));
    }

    [Fact]
    public async Task Check_NewerRelease_ReturnsItWithoutDownloadingOrInstalling()
    {
        var handler = Handler("1.10.4", () => throw new Xunit.Sdk.XunitException("must not download"));
        var (updater, store) = Create(handler);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        var release = await updater.CheckAsync(skipVersion: null, CancellationToken.None);

        Assert.Equal("1.10.4", release?.Version);
        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("1.10.3", null)]
    [InlineData("1.10.4", "1.10.4")]
    public async Task Check_UpToDateOrSkipped_ReturnsNull(string latest, string? skip)
    {
        var (updater, store) = Create(Handler(latest, () => ZipOk()));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        Assert.Null(await updater.CheckAsync(skip, CancellationToken.None));
    }

    // The user confirmed 1.10.4; a 1.10.5 published meanwhile must not be installed instead.
    [Fact]
    public async Task InstallChecked_InstallsExactlyTheConfirmedReleaseWithoutRefetching()
    {
        var apiCalls = 0;
        var zipBytes = ZipBytes();
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host != "api.github.com") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            apiCalls++;
            return StubHttpHandler.Json(LatestJson(apiCalls == 1 ? "1.10.4" : "1.10.5", FlowsealReleaseClientTests.Sha(zipBytes)));
        });
        var (updater, store) = Create(handler);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");
        var release = await updater.CheckAsync(null, CancellationToken.None);

        var result = await updater.InstallAsync(release!, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.Equal("1.10.4", store.ActiveVersion);
        Assert.Equal(1, apiCalls);
    }

    // Menu + balloon can both confirm the same release; the second install must not touch the running engine.
    [Fact]
    public async Task Install_ReleaseAlreadyActive_NoDownloadNoReinstall()
    {
        var handler = Handler("1.10.4", () => throw new Xunit.Sdk.XunitException("must not download"));
        var (updater, store) = Create(handler);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.4");
        var marker = Path.Combine(_root, "versions", "1.10.4", "marker.txt");
        File.WriteAllText(marker, "running");

        var result = await updater.InstallAsync(new ReleaseInfo("1.10.4", new Uri("https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip"), new string('0', 64)), null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Install_OlderThanActive_NoDowngrade()
    {
        var (updater, store) = Create(Handler("1.10.2", () => throw new Xunit.Sdk.XunitException("must not download")));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.4");

        var result = await updater.InstallAsync(
            new ReleaseInfo("1.10.2", new Uri("https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.2/zapret-discord-youtube-1.10.2.zip"), new string('0', 64)),
            null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal("1.10.4", store.ActiveVersion);
    }

    [Fact]
    public async Task Install_ChecksumMismatch_IsReleaseDefect()
    {
        var (updater, store) = Create(Handler("1.10.4", () => ZipOk(), digestOf: new byte[] { 0 }));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.True(ex.ReleaseDefect);
        Assert.Equal("1.10.4", ex.Version);
    }

    [Fact]
    public async Task Install_BrokenArchive_IsReleaseDefect()
    {
        var broken = EngineStoreTests.ValidRelease();
        broken.Remove("zapret-discord-youtube-1.10.3/bin/winws.exe");
        var (updater, store) = Create(Handler("1.10.4", () => ZipOk(broken), digestOf: ZipBytes(broken)));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.True(ex.ReleaseDefect);
    }

    // A flaky network must not make the app skip a good release forever.
    [Fact]
    public async Task Install_DownloadHttpError_IsNotReleaseDefect()
    {
        var (updater, store) = Create(Handler("1.10.4", () => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.False(ex.ReleaseDefect);
    }

    [Fact]
    public async Task Update_SameVersion_NoDownload()
    {
        var handler = Handler("1.10.3", () => throw new Xunit.Sdk.XunitException("must not download"));
        var (updater, store) = Create(handler);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        var result = await updater.UpdateAsync(null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Update_DownloadFails_ThrowsUpdateExceptionAndKeepsActive()
    {
        var (updater, store) = Create(Handler("1.10.4", () => new HttpResponseMessage(HttpStatusCode.NotFound)));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Fact]
    public async Task Update_BrokenArchive_ThrowsUpdateExceptionKeepsActiveAndSkipsCallback()
    {
        var broken = EngineStoreTests.ValidRelease();
        broken.Remove("zapret-discord-youtube-1.10.3/bin/winws.exe");
        var (updater, store) = Create(Handler("1.10.4", () => ZipOk(broken), digestOf: ZipBytes(broken)));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");
        var called = false;

        await Assert.ThrowsAsync<UpdateException>(() =>
            updater.UpdateAsync(_ => { called = true; return Task.CompletedTask; }, CancellationToken.None));

        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.False(called);
    }

    [Fact]
    public async Task Update_SwitchCallbackSeesNewActiveWhileOldStillOnDisk()
    {
        var (updater, store) = Create(Handler("1.10.3", () => ZipOk()));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.2");
        var oldDir = Path.Combine(_root, "versions", "1.10.2");
        bool? oldExisted = null;
        string? activeDuringCallback = null;

        await updater.UpdateAsync(_ =>
        {
            oldExisted = Directory.Exists(oldDir);
            activeDuringCallback = store.ActiveVersion;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(oldExisted);
        Assert.Equal("1.10.3", activeDuringCallback);
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public async Task Update_SwitchFails_RollsBackToPreviousAndKeepsItOnDisk()
    {
        var (updater, store) = Create(Handler("1.10.3", () => ZipOk()));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.2");
        string? rolledBackTo = null;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(
            _ => throw new InvalidOperationException("winws exited with code 1"),
            CancellationToken.None,
            previous => { rolledBackTo = previous.Version; return Task.CompletedTask; }));

        // A release whose winws will not start is defective: it must not be re-offered on every launch.
        Assert.True(ex.ReleaseDefect);
        Assert.Equal("1.10.3", ex.Version);

        Assert.Equal("1.10.2", store.ActiveVersion);
        Assert.Equal("1.10.2", rolledBackTo);
        Assert.True(Directory.Exists(Path.Combine(_root, "versions", "1.10.2")));
    }

    [Fact]
    public async Task Update_GitHubTimeout_SurfacesAsUpdateException()
    {
        var slow = new DelayingHandler(TimeSpan.FromSeconds(5));
        var store = new EngineStore(_root);
        var updater = new EngineUpdater(
            new FlowsealReleaseClient(new HttpClient(slow) { Timeout = TimeSpan.FromMilliseconds(100) }), store);

        await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Update_ZipDownloadTimesOut_UpdateExceptionAndActiveUnchanged()
    {
        var api = Handler("1.10.4", () => ZipOk());
        var slowZip = new RoutingHandler(req => req.RequestUri!.Host == "api.github.com", api, new DelayingHandler(TimeSpan.FromSeconds(5)));
        var store = new EngineStore(_root);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");
        var updater = new EngineUpdater(
            new FlowsealReleaseClient(new HttpClient(slowZip) { Timeout = TimeSpan.FromMilliseconds(300) }), store);

        await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    // HttpClient.Timeout no longer applies after headers; a body that stalls must still end the update.
    [Fact]
    public async Task Update_ZipBodyStallsAfterHeaders_DeadlineEndsItWithUpdateException()
    {
        var handler = Handler("1.10.4", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) });
        var store = new EngineStore(_root);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");
        var updater = new EngineUpdater(
            new FlowsealReleaseClient(new HttpClient(handler), downloadDeadline: TimeSpan.FromMilliseconds(300)), store);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.Equal("1.10.4", ex.Version);
        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Fact]
    public async Task Update_LatestIsSkippedVersion_NoDownload()
    {
        var handler = Handler("1.10.4", () => throw new Xunit.Sdk.XunitException("must not download"));
        var (updater, store) = Create(handler);
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        var result = await updater.UpdateAsync(null, CancellationToken.None, skipVersion: "1.10.4");

        Assert.Equal(UpdateOutcome.Skipped, result.Outcome);
        Assert.Single(handler.Requests);
    }

    private sealed class NeverEndingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, bool> _first;
        private readonly HttpMessageInvoker _a;
        private readonly HttpMessageInvoker _b;

        public RoutingHandler(Func<HttpRequestMessage, bool> first, HttpMessageHandler a, HttpMessageHandler b)
        {
            _first = first;
            _a = new HttpMessageInvoker(a);
            _b = new HttpMessageInvoker(b);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            (_first(request) ? _a : _b).SendAsync(request, ct);
    }

    [Fact]
    public async Task Update_CallerCancels_PropagatesCancellation()
    {
        var slow = new DelayingHandler(TimeSpan.FromSeconds(5));
        var updater = new EngineUpdater(new FlowsealReleaseClient(new HttpClient(slow)), new EngineStore(_root));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.UpdateAsync(null, cts.Token));
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayingHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Update_TamperedArchive_RejectedBeforeInstall()
    {
        var (updater, store) = Create(Handler("1.10.4", () => ZipOk(), digestOf: new byte[] { 0 }));
        store.Install(EngineStoreTests.Zip(EngineStoreTests.ValidRelease()), "1.10.3");

        await Assert.ThrowsAsync<UpdateException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.10.4")));
    }

    [Fact]
    public async Task Update_NothingInstalled_InstallsLatest()
    {
        var (updater, store) = Create(Handler("1.10.3", () => ZipOk()));

        var result = await updater.UpdateAsync(null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.NotNull(store.GetActive());
    }
}
