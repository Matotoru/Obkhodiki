using System.Net;
using ZapretHub.Core.Testing;
using ZapretHub.Core.Tests.TestDoubles;

namespace ZapretHub.Core.Tests.Testing;

public class HttpConnectivityProbeTests
{
    private static readonly ProbeTarget Target = new("t", new Uri("https://t.test"));

    private static HttpResponseMessage Body(Stream s, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StreamContent(s) };

    [Fact]
    public async Task Probe_SmallPageDownloaded_Ok()
    {
        var probe = new HttpConnectivityProbe(new HttpClient(new StubHttpHandler(_ => Body(new MemoryStream(new byte[1000])))), TimeSpan.FromSeconds(5));

        var result = await probe.ProbeAsync(Target, CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Probe_AnyHttpStatusMeansReachable(HttpStatusCode code)
    {
        var probe = new HttpConnectivityProbe(new HttpClient(new StubHttpHandler(_ => Body(new MemoryStream(), code))), TimeSpan.FromSeconds(5));

        Assert.True((await probe.ProbeAsync(Target, CancellationToken.None)).Ok);
    }

    // Keep-alive reuse would skip the TLS handshake, so DPI would never judge the strategy under test.
    [Fact]
    public async Task Probe_AlwaysAsksForFreshConnection()
    {
        var handler = new StubHttpHandler(_ => Body(new MemoryStream()));
        var probe = new HttpConnectivityProbe(new HttpClient(handler), TimeSpan.FromSeconds(5));

        await probe.ProbeAsync(Target, CancellationToken.None);
        await probe.ProbeAsync(Target, CancellationToken.None);

        Assert.All(handler.Requests, r => Assert.True(r.Headers.ConnectionClose));
    }

    [Fact]
    public void NonPoolingHandler_DisablesConnectionReuse()
    {
        var handler = (SocketsHttpHandler)HttpConnectivityProbe.CreateNonPoolingHandler();

        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionIdleTimeout);
        // A redirect to another host would test that host instead of the target.
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task Probe_EndlessBody_StopsAfterReadCapAndSucceeds()
    {
        var endless = new CountingEndlessStream();
        var probe = new HttpConnectivityProbe(new HttpClient(new StubHttpHandler(_ => Body(endless))), TimeSpan.FromSeconds(5));

        var result = await probe.ProbeAsync(Target, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.InRange(endless.BytesRead, 64 * 1024, 1024 * 1024);
        Assert.True(result.Latency > TimeSpan.Zero);
    }

    // TSPU throttling pattern: TLS handshake and first ~16 KB pass, then the connection stalls.
    [Fact]
    public async Task Probe_StallsAfterFirstKilobytes_Fails()
    {
        var stalling = new StallingStream(bytesBeforeStall: 16 * 1024);
        var probe = new HttpConnectivityProbe(new HttpClient(new StubHttpHandler(_ => Body(stalling))), TimeSpan.FromMilliseconds(300));

        var result = await probe.ProbeAsync(Target, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Probe_ConnectionError_Fails()
    {
        var probe = new HttpConnectivityProbe(new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("reset"))), TimeSpan.FromSeconds(5));

        Assert.False((await probe.ProbeAsync(Target, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Probe_CallerCancellation_Propagates()
    {
        var probe = new HttpConnectivityProbe(new HttpClient(new StubHttpHandler(_ => Body(new StallingStream(0)))), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(Target, cts.Token));
    }

    private sealed class CountingEndlessStream : StallingStream
    {
        public CountingEndlessStream() : base(int.MaxValue) { }
    }

    private class StallingStream : Stream
    {
        public long BytesRead { get; private set; }

        private int _left;
        public StallingStream(int bytesBeforeStall) => _left = bytesBeforeStall;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left <= 0)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            var n = Math.Min(_left, buffer.Length);
            _left -= n;
            BytesRead += n;
            await Task.Yield();
            return n;
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
}
