using System.Text;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Obkhodiki.Core.Games;

namespace Obkhodiki.App;

/// <summary>Built-in game profiles and their card pictures.</summary>
internal sealed partial class AppController
{
    private const long MaxImageBytes = 20L * 1024 * 1024;
    private const int CoverWidth = 920;
    private readonly SemaphoreSlim _coverGate = new(2, 2);

    public static string CoversDir => Path.Combine(AppPaths.Root, "covers");
    public static string CustomCoverPath(string profileId) =>
        GameProfiles.IsValidId(profileId) ? Path.Combine(CoversDir, $"custom-{profileId}.png") : throw new ArgumentException($"Invalid profile id '{profileId}'.");
    private static string SteamCoverPath(int appId) => Path.Combine(CoversDir, $"steam-{appId}.png");

    /// <summary>The picture for a card: the user's own, else the cached Steam cover; null when there is none (yet).</summary>
    public static string? CoverFor(string? profileId, GameCatalogEntry? entry)
    {
        if (profileId is not null && GameProfiles.IsValidId(profileId) && File.Exists(CustomCoverPath(profileId))) return CustomCoverPath(profileId);
        return entry?.SteamAppId is { } app && File.Exists(SteamCoverPath(app)) ? SteamCoverPath(app) : null;
    }

    /// <summary>Downloads the Steam cover once (outside the operation queue: it must not block anything).</summary>
    public async Task<string?> EnsureCoverAsync(GameCatalogEntry entry)
    {
        if (entry.SteamAppId is not { } app) return null;
        var path = SteamCoverPath(app);
        if (File.Exists(path)) return path;
        await _coverGate.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            string? header = null;
            try
            {
                header = await DownloadThroughAnyPathAsync(async (client, ct) =>
                    GameCatalog.ParseHeaderImage(await client.GetStringAsync(GameCatalog.AppDetailsUrl(app), ct), app));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
            {
                Log.Info($"Steam store did not answer for {app}: {ex.Message}");
            }
            foreach (var url in GameCatalog.CoverUrls(app, header))
            {
                try
                {
                    var bytes = await DownloadThroughAnyPathAsync(async (client, ct) =>
                    {
                        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("Картинка слишком большая.");
                        return await response.Content.ReadAsByteArrayAsync(ct);
                    });
                    SavePicture(bytes, path);
                    return path;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or NotSupportedException or FileFormatException or ArgumentException)
                {
                    Log.Info($"Cover {url} failed: {ex.Message}");
                }
            }
            return null;
        }
        finally
        {
            _coverGate.Release();
        }
    }

    /// <summary>Takes a picture the user picked for a game card (validated and re-encoded, never used as is).</summary>
    public async Task SetCustomCoverAsync(string profileId, string sourceFile)
    {
        var info = new FileInfo(sourceFile);
        if (!info.Exists || info.Length > MaxImageBytes) throw new InvalidDataException("Файл не найден или больше 20 МБ.");
        var bytes = await File.ReadAllBytesAsync(sourceFile);
        await Task.Run(() => SavePicture(bytes, CustomCoverPath(profileId)));
        Changed();
    }

    public void RemoveCustomCover(string profileId)
    {
        var path = CustomCoverPath(profileId);
        if (File.Exists(path)) File.Delete(path);
        Changed();
    }

    /// <summary>
    /// Decodes an image and writes it back as a scaled-down PNG: whatever came in (a download, a user's file) is
    /// only ever shown after passing through the decoder, and the app never keeps the original bytes.
    /// </summary>
    private static void SavePicture(byte[] bytes, string path)
    {
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new InvalidDataException("Пустая или слишком большая картинка.");
        using var input = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth is < 16 or > 10000 || frame.PixelHeight is < 16 or > 10000) throw new InvalidDataException("Неподходящий размер картинки.");
        if (frame.PixelWidth > CoverWidth)
        {
            var scale = (double)CoverWidth / frame.PixelWidth;
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var output = File.Create(temp)) encoder.Save(output);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Adds a game from the catalog. Games on their own network get its address list right away and work at once;
    /// the others are added switched off, to be recorded during a match.
    /// </summary>
    /// <returns>The new profile, or null when it could not be added (the error is shown).</returns>
    public async Task<GameProfile?> AddCatalogGameAsync(GameCatalogEntry entry, string? runningExe)
    {
        GameProfile? added = null;
        await Serialized($"Добавление {entry.Name}…", silentErrors: false, async () =>
        {
            if (Settings.GameProfiles.Any(p => GameCatalog.For(p)?.Id == entry.Id)) throw new InvalidOperationException($"«{entry.Name}» уже добавлена.");
            var prefixes = new List<string>();
            foreach (var asn in entry.Asns)
            {
                try
                {
                    prefixes.AddRange(await DownloadThroughAnyPathAsync(async (client, ct) =>
                        GameCatalog.ParsePrefixes(await client.GetStringAsync(GameCatalog.PrefixesUrl(asn), ct))));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException or InvalidDataException)
                {
                    throw new InvalidOperationException($"Не удалось получить адреса серверов {entry.Publisher} (RIPE): {ex.Message}. Попробуйте позже или запишите игру вручную.", ex);
                }
            }

            var profile = new GameProfile
            {
                Id = GameProfiles.MakeId(entry.Id, Settings.GameProfiles.Select(p => p.Id)),
                Name = entry.Name,
                ProcessName = runningExe ?? entry.ProcessNames[0],
                TcpPorts = entry.TcpPorts,
                UdpPorts = entry.UdpPorts,
                Route = GameRoute.Direct,
                Enabled = prefixes.Count > 0,
            };
            Directory.CreateDirectory(AppPaths.GamesDir);
            var content = new StringBuilder()
                .AppendLine($"# {entry.Name}: game server addresses (one IP or CIDR per line)")
                .AppendLine(entry.Asns.Count > 0 ? $"# From the announced networks of {string.Join(", ", entry.Asns.Select(a => "AS" + a))} (RIPEstat), {DateTime.Now:yyyy-MM-dd}" : "# Record a match to fill this list")
                .AppendJoin(Environment.NewLine, prefixes)
                .AppendLine()
                .ToString();
            await File.WriteAllTextAsync(GameIpsetPath(profile.Id), content);
            Settings.GameProfiles.Add(profile);
            _settingsStore.Save(Settings);
            Log.Info($"Catalog game {entry.Id} added as {profile.Id}: {prefixes.Count} networks");
            added = profile;

            await ReapplyVpnAfterProfileChangeAsync();
            if (_runner.IsRunning && profile.Enabled) await StartCoreAsync();
        });
        if (added is { Enabled: true }) Notify?.Invoke(entry.Name, "Готово: игра добавлена и включена.", ToolTipIcon.Info);
        return added;
    }
}
