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
            var (prefixes, udpPorts, origin) = await CatalogAddressesAsync(entry);

            var profile = new GameProfile
            {
                Id = GameProfiles.MakeId(entry.Id, Settings.GameProfiles.Select(p => p.Id)),
                Name = entry.Name,
                ProcessName = runningExe ?? entry.ProcessNames[0],
                TcpPorts = entry.TcpPorts,
                UdpPorts = udpPorts,
                Route = GameRoute.Direct,
                Enabled = prefixes.Count > 0,
            };
            Directory.CreateDirectory(AppPaths.GamesDir);
            await File.WriteAllTextAsync(GameIpsetPath(profile.Id), CatalogAddressFile(entry, prefixes, origin));
            Settings.GameProfiles.Add(profile);
            _settingsStore.Save(Settings);
            Log.Info($"Catalog game {entry.Id} added as {profile.Id}: {prefixes.Count} addresses from {origin}");
            added = profile;

            await ReapplyVpnAfterProfileChangeAsync();
            if (_runner.IsRunning && profile.Enabled) await StartCoreAsync();
        });
        if (added is { Enabled: true }) Notify?.Invoke(entry.Name, "Готово: игра добавлена и включена.", ToolTipIcon.Info);
        return added;
    }

    /// <summary>
    /// Addresses of a catalog game: Steam's exact relay list for SDR games (falling back to the publisher's network),
    /// the announced prefixes of the publisher's network for the others, nothing for games that need recording.
    /// </summary>
    private async Task<(List<string> Addresses, string UdpPorts, string Origin)> CatalogAddressesAsync(GameCatalogEntry entry)
    {
        if (entry.UsesSdr && entry.SteamAppId is { } app)
        {
            try
            {
                var (relays, ports) = await DownloadThroughAnyPathAsync(async (client, ct) =>
                    GameCatalog.ParseSdrConfig(await client.GetStringAsync(GameCatalog.SdrConfigUrl(app), ct)));
                return (relays.ToList(), ports, "Steam GetSDRConfig");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException or InvalidDataException)
            {
                Log.Info($"GetSDRConfig for {app} failed, using the publisher's network: {ex.Message}");
            }
        }
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
        return (prefixes, entry.UdpPorts, entry.Asns.Count > 0 ? string.Join(", ", entry.Asns.Select(a => "AS" + a)) + " (RIPEstat)" : "recording");
    }

    private static string CatalogAddressFile(GameCatalogEntry entry, IReadOnlyList<string> addresses, string origin) => new StringBuilder()
        .AppendLine($"# {entry.Name}: game server addresses (one IP or CIDR per line)")
        .AppendLine(addresses.Count > 0 ? $"# From {origin}, {DateTime.Now:yyyy-MM-dd}" : "# Record a match to fill this list")
        .AppendJoin(Environment.NewLine, addresses)
        .AppendLine()
        .ToString();

    public async Task ExportGameAsync(string id, string path)
    {
        var profile = Settings.GameProfiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Профиль не найден.");
        var file = GameIpsetPath(id);
        var addresses = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
        await File.WriteAllTextAsync(path, GameShare.Create(profile, addresses, SelfUpdate.CurrentVersion).Serialize());
        Log.Info($"Game profile {id} exported to {path}");
    }

    public static async Task<GameShare> ReadGameFileAsync(string path)
    {
        if (new FileInfo(path).Length > GameShare.MaxFileBytes) throw new FormatException("Файл слишком большой для профиля игры.");
        return GameShare.Parse(await File.ReadAllTextAsync(path));
    }

    /// <summary>The profile an imported game would replace: same id, or the same catalog game.</summary>
    public GameProfile? ExistingFor(GameShare share) =>
        Settings.GameProfiles.FirstOrDefault(p => p.Id == share.Profile.Id)
        ?? (GameCatalog.For(share.Profile) is { } entry ? Settings.GameProfiles.FirstOrDefault(p => GameCatalog.For(p)?.Id == entry.Id) : null);

    /// <summary>Adds the shared game, or replaces the matching one (keeping whether it is switched on).</summary>
    public Task ImportGameAsync(GameShare share) => Serialized($"Загрузка {share.Profile.Name}…", silentErrors: false, async () =>
    {
        var profile = share.Profile;
        var existing = ExistingFor(share);
        if (existing is not null)
        {
            profile.Id = existing.Id;
            profile.Enabled = existing.Enabled;
            Settings.GameProfiles[Settings.GameProfiles.IndexOf(existing)] = profile;
        }
        else
        {
            profile.Id = GameProfiles.MakeId(profile.Id, Settings.GameProfiles.Select(p => p.Id));
            Settings.GameProfiles.Add(profile);
        }
        Directory.CreateDirectory(AppPaths.GamesDir);
        await File.WriteAllTextAsync(GameIpsetPath(profile.Id), share.Addresses + Environment.NewLine);
        _settingsStore.Save(Settings);
        Log.Info($"Game profile {profile.Id} imported ({(existing is null ? "new" : "replaced")})");
        await ReapplyVpnAfterProfileChangeAsync();
        if (_runner.IsRunning && profile.Enabled) await StartCoreAsync();
    });

    private static readonly TimeSpan SdrRefreshAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Valve moves relays now and then: refreshes the lists of SDR games added from the catalog once a week.
    /// The new list is used from the next bypass start (a running winws is not restarted for it).
    /// </summary>
    private async Task RefreshSdrAddressesAsync()
    {
        foreach (var profile in Settings.GameProfiles.ToList())
        {
            if (GameCatalog.For(profile) is not { UsesSdr: true, SteamAppId: { } app } entry) continue;
            var path = GameIpsetPath(profile.Id);
            if (!File.Exists(path) || DateTime.Now - File.GetLastWriteTime(path) < SdrRefreshAge) continue;
            // Only lists this app wrote: a list the user recorded or edited by hand is theirs.
            var firstLines = File.ReadLines(path).Take(2).ToList();
            if (firstLines.Count < 2 || !firstLines[1].StartsWith("# From Steam GetSDRConfig", StringComparison.Ordinal)) continue;
            try
            {
                var (relays, ports) = await DownloadThroughAnyPathAsync(async (client, ct) =>
                    GameCatalog.ParseSdrConfig(await client.GetStringAsync(GameCatalog.SdrConfigUrl(app), ct)));
                await File.WriteAllTextAsync(path, CatalogAddressFile(entry, relays, "Steam GetSDRConfig"));
                profile.UdpPorts = PortSet.Parse(profile.UdpPorts).Union(PortSet.Parse(ports)).ToString();
                _settingsStore.Save(Settings);
                Log.Info($"SDR relays of {profile.Id} refreshed: {relays.Count} addresses, UDP {profile.UdpPorts}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException or InvalidDataException or IOException)
            {
                Log.Info($"SDR refresh for {profile.Id} failed: {ex.Message}");
            }
        }
    }
}
