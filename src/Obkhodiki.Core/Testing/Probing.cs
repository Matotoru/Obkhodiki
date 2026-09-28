using System.Diagnostics;

namespace Obkhodiki.Core.Testing;

public sealed record ProbeTarget(string Name, Uri Url);

public sealed record ProbeResult(bool Ok, TimeSpan Latency);

public interface IConnectivityProbe
{
    Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken ct);
}

/// <summary>
/// A target counts as reachable when the HTTPS response arrives and its body (up to 64 KB)
/// downloads within the timeout. Reading past 16 KB matters: TSPU often lets the handshake
/// and first ~16 KB through and then silently stalls the connection.
/// </summary>
public sealed class HttpConnectivityProbe : IConnectivityProbe
{
    private const int ReadLimit = 64 * 1024;

    /// <summary>Handler that never pools connections, so each probe performs a full TLS handshake.</summary>
    public static HttpMessageHandler CreateNonPoolingHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.Zero,
        PooledConnectionIdleTimeout = TimeSpan.Zero,
        AllowAutoRedirect = false,
        // A system proxy would carry the traffic past the DPI and make every strategy look good.
        UseProxy = false,
        // Like a browser: one unreachable IP of a host must not fail a target the user can open fine.
        ConnectCallback = StaggeredConnect.ConnectAsync,
    };

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpConnectivityProbe(HttpClient http, TimeSpan timeout)
    {
        _http = http;
        _timeout = timeout;
    }

    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            // A reused keep-alive connection would skip the TLS ClientHello, so DPI would never see the
            // new strategy at work. Every probe must open a fresh connection.
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            request.Headers.ConnectionClose = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Any HTTP status proves the path through DPI works; 5xx is still the server answering.
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            var total = 0;
            int read;
            while (total < ReadLimit && (read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
            }
            return new ProbeResult(true, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, sw.Elapsed);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, sw.Elapsed);
        }
        catch (IOException)
        {
            return new ProbeResult(false, sw.Elapsed);
        }
    }
}
