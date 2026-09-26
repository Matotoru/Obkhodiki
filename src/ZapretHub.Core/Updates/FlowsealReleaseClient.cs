using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretHub.Core.Updates;

public sealed record ReleaseInfo(string Version, Uri ZipUrl, string Sha256);

/// <summary>
/// Fetches Flowseal releases. The downloaded engine is executed with administrator rights,
/// so the asset URL is pinned to the official repository and the archive is checked
/// against the SHA-256 digest published by the GitHub API.
/// </summary>
public sealed partial class FlowsealReleaseClient
{
    public const long MaxDownloadBytes = 50L * 1024 * 1024;

    private static readonly Uri LatestUri = new("https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest");
    private const string DownloadPathPrefix = "/Flowseal/zapret-discord-youtube/releases/download/";

    [GeneratedRegex(@"^zapret-discord-youtube-[0-9.]+\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetName();

    [GeneratedRegex(@"^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Digest();

    private readonly HttpClient _http;
    private readonly TimeSpan _downloadDeadline;

    /// <param name="downloadDeadline">Whole-download limit. HttpClient.Timeout stops applying once headers
    /// arrive, and a TSPU-style stall mid-body would otherwise hang the update forever.</param>
    public FlowsealReleaseClient(HttpClient http, TimeSpan? downloadDeadline = null)
    {
        _http = http;
        _downloadDeadline = downloadDeadline ?? TimeSpan.FromMinutes(3);
    }

    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct)
    {
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUri);
            // GitHub API rejects requests without a User-Agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ZapretHub", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub returned {(int)response.StatusCode} for the latest Flowseal release.");
            }
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Could not reach GitHub to check Flowseal updates.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("GitHub did not answer in time.", ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = ReleaseVersion.Normalize(root.GetProperty("tag_name").GetString() ?? "");
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!AssetName().IsMatch(name)) continue;

                var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
                if (url.Scheme != Uri.UriSchemeHttps
                    || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || !url.AbsolutePath.StartsWith(DownloadPathPrefix, StringComparison.Ordinal))
                {
                    throw new UpdateException($"Asset URL '{url}' is not in the official Flowseal repository.");
                }

                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                var match = Digest().Match(digest);
                if (!match.Success)
                {
                    throw new UpdateException($"Flowseal release {version} has no SHA-256 digest; refusing to install unverified files.");
                }
                return new ReleaseInfo(version, url, match.Groups[1].Value.ToLowerInvariant());
            }
            throw new UpdateException($"Flowseal release {version} has no zapret-discord-youtube-*.zip asset.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or UriFormatException)
        {
            throw new UpdateException("Unexpected response format from GitHub releases API.", ex);
        }
    }

    public async Task<Stream> DownloadAsync(ReleaseInfo release, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_downloadDeadline);
        var token = deadline.Token;
        try
        {
            using var response = await _http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"Download of Flowseal {release.Version} failed: HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new UpdateException($"Flowseal {release.Version} archive is larger than {MaxDownloadBytes / 1024 / 1024} MB.");
            }

            var buffer = new MemoryStream();
            await using (var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxDownloadBytes)
                    {
                        throw new UpdateException($"Flowseal {release.Version} archive is larger than {MaxDownloadBytes / 1024 / 1024} MB.");
                    }
                    buffer.Write(chunk, 0, read);
                }
            }

            var actual = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))).ToLowerInvariant();
            if (actual != release.Sha256)
            {
                throw new UpdateException($"Flowseal {release.Version} archive checksum mismatch (expected {release.Sha256}, got {actual}).", null, release.Version, releaseDefect: true);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Download of Flowseal {release.Version} failed.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException($"Download of Flowseal {release.Version} timed out.", ex);
        }
    }
}
