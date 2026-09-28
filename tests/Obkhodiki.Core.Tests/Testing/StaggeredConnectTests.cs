using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Obkhodiki.Core.Testing;

namespace Obkhodiki.Core.Tests.Testing;

public class StaggeredConnectTests
{
    private sealed class Conn : IDisposable
    {
        public Conn(string name) => Name = name;
        public string Name { get; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    // "hang": never answers (a blocked IP); "fail": refused at once; "ok": answers after the given delay.
    private static Func<(string Name, string Kind, int Ms), CancellationToken, Task<Conn>> Fake(List<Conn> made) =>
        async (c, ct) =>
        {
            switch (c.Kind)
            {
                case "hang":
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException();
                case "fail":
                    await Task.Yield();
                    throw new SocketException((int)SocketError.ConnectionRefused);
                default:
                    await Task.Delay(c.Ms, ct);
                    var conn = new Conn(c.Name);
                    lock (made) made.Add(conn);
                    return conn;
            }
        };

    [Fact]
    public async Task BlockedFirstAddress_SecondAnswersSoonAfterStagger()
    {
        var sw = Stopwatch.StartNew();
        var result = await StaggeredConnect.FirstAsync(
            new[] { ("blocked", "hang", 0), ("good", "ok", 10) }, Fake(new()), TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal("good", result.Name);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task RefusedFirstAddress_NextStartsWithoutWaitingForStagger()
    {
        var sw = Stopwatch.StartNew();
        var result = await StaggeredConnect.FirstAsync(
            new[] { ("refused", "fail", 0), ("good", "ok", 0) }, Fake(new()), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("good", result.Name);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task FastFirstAddress_IsUsedAndLaterOnesNotNeeded()
    {
        var made = new List<Conn>();
        var result = await StaggeredConnect.FirstAsync(
            new[] { ("first", "ok", 0), ("second", "ok", 0) }, Fake(made), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("first", result.Name);
        Assert.Single(made);
    }

    [Fact]
    public async Task LosingConnection_IsDisposed()
    {
        var made = new List<Conn>();
        var result = await StaggeredConnect.FirstAsync(
            new[] { ("slow", "ok", 400), ("fast", "ok", 0) }, Fake(made), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await Task.Delay(700);

        Assert.Equal("fast", result.Name);
        lock (made) Assert.All(made.Where(c => c.Name == "slow"), c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task AllRefused_Throws()
    {
        await Assert.ThrowsAsync<SocketException>(() => StaggeredConnect.FirstAsync(
            new[] { ("a", "fail", 0), ("b", "fail", 0) }, Fake(new()), TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StaggeredConnect.FirstAsync(
            new[] { ("a", "hang", 0), ("b", "hang", 0) }, Fake(new()), TimeSpan.FromMilliseconds(20), cts.Token));
    }

    // The probe's handler still speaks HTTP through the custom connect.
    [Fact]
    public async Task Handler_ConnectsThroughStaggeredConnect()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            // Read the request headers up to the blank line, then answer.
            var request = new List<byte>();
            var buf = new byte[1];
            while (!request.TakeLast(4).SequenceEqual("

"u8.ToArray()) && await s.ReadAsync(buf) == 1) request.Add(buf[0]);
            await s.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
        });

        var made = new List<Conn>();
        var result = await StaggeredConnect.FirstAsync(
            new[] { ("hang", "hang", 0), ("ok", "ok", 0) }, Fake(made), StaggeredConnect.DefaultStagger, CancellationToken.None);
        Assert.Equal("ok", result.Name);

        using var http = new HttpClient(HttpConnectivityProbe.CreateNonPoolingHandler());
        using var response = await http.GetAsync($"http://127.0.0.1:{port}/");
        await server;
        listener.Stop();
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
