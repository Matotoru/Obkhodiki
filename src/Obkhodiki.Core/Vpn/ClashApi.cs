using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Obkhodiki.Core.Vpn;

/// <summary>Local control port of sing-box (Clash API): per-run port and secret, loopback only.</summary>
public sealed record ClashApiOptions(int Port, string Secret)
{
    public static ClashApiOptions Random(int port) =>
        new(port, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    public override string ToString() => $"127.0.0.1:{Port}";
}

/// <param name="Chains">Outbounds the connection went through, innermost first (e.g. server tag, "proxy"); ["direct"] when not via the VPS.</param>
public sealed record ClashConnection(string Id, long Upload, long Download, IReadOnlyList<string> Chains)
{
    public bool ViaVpn => Chains.Count > 0 && !Chains.Contains("direct") && !Chains.Contains("block");
}

/// <summary>
/// Adds up the traffic of connections that went through the VPS. The API only lists open connections, so the
/// counters are sampled regularly and only growth is added; the last bytes of a connection that closes between
/// two samples are missed (a small undercount).
/// </summary>
public sealed class VpnTrafficMeter
{
    private Dictionary<string, (long Up, long Down)> _seen = new();

    public long Upload { get; private set; }
    public long Download { get; private set; }

    public void Update(IReadOnlyList<ClashConnection> connections)
    {
        var next = new Dictionary<string, (long Up, long Down)>();
        foreach (var c in connections.Where(c => c.ViaVpn))
        {
            var before = _seen.TryGetValue(c.Id, out var b) ? b : (0L, 0L);
            Upload += Math.Max(0, c.Upload - before.Item1);
            Download += Math.Max(0, c.Download - before.Item2);
            next[c.Id] = (c.Upload, c.Download);
        }
        _seen = next;
    }

    public void Reset()
    {
        _seen = new();
        Upload = 0;
        Download = 0;
    }
}

/// <summary>Switches the active server and measures servers through the running sing-box.</summary>
public sealed class ClashApiClient
{
    public const string DelayUrl = "https://www.gstatic.com/generate_204";

    private readonly HttpClient _http;
    private readonly ClashApiOptions _options;

    /// <param name="http">Must not use a system proxy (the API is on loopback).</param>
    public ClashApiClient(HttpClient http, ClashApiOptions options)
    {
        _http = http;
        _options = options;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_options.Port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Secret);
        return request;
    }

    /// <summary>Makes <paramref name="tag"/> the active outbound of the selector <paramref name="selector"/>.</summary>
    public async Task SelectAsync(string selector, string tag, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, $"/proxies/{Uri.EscapeDataString(selector)}");
        request.Content = JsonContent.Create(new { name = tag });
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Open connections with their byte counters and outbound chain, or null when the API did not answer.</summary>
    public async Task<IReadOnlyList<ClashConnection>?> ConnectionsAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/connections");
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ParseConnections(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static IReadOnlyList<ClashConnection> ParseConnections(JsonElement root)
    {
        var result = new List<ClashConnection>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array) return result;
        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("id", out var id) || id.GetString() is not { } idText) continue;
            long Counter(string name) => c.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) && n >= 0 ? n : 0;
            var chains = c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray().Select(e => e.GetString()).Where(e => e is not null).Select(e => e!).ToList()
                : new List<string>();
            result.Add(new ClashConnection(idText, Counter("upload"), Counter("download"), chains));
        }
        return result;
    }

    /// <summary>Round trip of an HTTPS request through the server, or null when it failed or timed out.</summary>
    public async Task<int?> DelayAsync(string tag, TimeSpan timeout, CancellationToken ct, string url = DelayUrl)
    {
        var ms = ((int)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        using var request = Request(HttpMethod.Get, $"/proxies/{Uri.EscapeDataString(tag)}/delay?timeout={ms}&url={Uri.EscapeDataString(url)}");
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("delay", out var d) && d.TryGetInt32(out var delay) && delay > 0 ? delay : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Ping result of one server over a few samples.</summary>
public sealed record ServerPing(string Tag, int? MedianMs, int Successes, int Samples)
{
    /// <summary>Most samples got through: a single lucky answer is not enough to switch to a server.</summary>
    public bool Reliable => Samples > 0 && Successes * 2 > Samples;

    public static ServerPing From(string tag, IReadOnlyList<int?> samples)
    {
        var ok = samples.Where(s => s is not null).Select(s => s!.Value).OrderBy(v => v).ToList();
        return new ServerPing(tag, ok.Count == 0 ? null : ok[ok.Count / 2], ok.Count, samples.Count);
    }
}

/// <summary>Picks the server to use. Switches only for a clear gain, so the tunnel does not flap between equals.</summary>
public static class ServerRanker
{
    public const int MinGainMs = 20;
    public const double MinGainRatio = 0.2;

    /// <returns>The tag to switch to, or null to stay.</returns>
    public static string? ChooseBetter(string? currentTag, IReadOnlyList<ServerPing> pings)
    {
        var best = pings.Where(p => p.Reliable && p.MedianMs is not null).OrderBy(p => p.MedianMs).FirstOrDefault();
        if (best is null || best.Tag == currentTag) return null;
        var current = pings.FirstOrDefault(p => p.Tag == currentTag);
        if (current is not { Reliable: true, MedianMs: { } currentMs }) return best.Tag;
        var gain = currentMs - best.MedianMs!.Value;
        return gain >= MinGainMs && gain >= currentMs * MinGainRatio ? best.Tag : null;
    }
}
