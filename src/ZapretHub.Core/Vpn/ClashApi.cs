using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ZapretHub.Core.Vpn;

/// <summary>Local control port of sing-box (Clash API): per-run port and secret, loopback only.</summary>
public sealed record ClashApiOptions(int Port, string Secret)
{
    public static ClashApiOptions Random(int port) =>
        new(port, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    public override string ToString() => $"127.0.0.1:{Port}";
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
