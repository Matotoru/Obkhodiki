using System.Windows.Forms;
using ZapretHub.Core.Vpn;

namespace ZapretHub.App;

/// <summary>What the VPS page shows about the source; no URL, links or passwords.</summary>
public sealed record VpnSourceView(
    bool IsSubscription,
    string Title,
    long? Used,
    long? Total,
    DateTimeOffset? Expire,
    DateTimeOffset? FetchedAt,
    int Skipped,
    bool HasInsecure);

/// <summary>Subscriptions, server pings and automatic choice of the fastest server.</summary>
internal sealed partial class AppController
{
    public static readonly TimeSpan AutoBestInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SubscriptionRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RuleRetryMissing = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RuleRefreshCheck = TimeSpan.FromHours(6);
    private const int PingSamples = 3;
    private const int PingParallel = 4;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(4);
    private const long MaxSubscriptionSize = 2 * 1024 * 1024;

    private readonly Dictionary<string, ServerPing> _pings = new();
    private DateTime _nextBestCheck = DateTime.MaxValue;
    private DateTime _lastSubscriptionAttempt = DateTime.MinValue;
    private DateTime _lastRuleAttempt = DateTime.MinValue;
    private int _pinging;
    private CancellationTokenSource? _autoPingCts;
    private Task? _autoPing;

    /// <summary>Last ping per server tag (UI thread).</summary>
    public IReadOnlyDictionary<string, ServerPing> ServerPings => _pings;
    public DateTime? LastPingTime { get; private set; }
    public bool IsPingingServers => Volatile.Read(ref _pinging) > 0;

    // ---------- subscription ----------

    private sealed record SubscriptionFetch(SubscriptionContent Content, string? Title, SubscriptionUsage? Usage, TimeSpan Interval);

    private async Task<string?> SetSubscriptionAsync(string url)
    {
        Uri uri;
        try
        {
            uri = SubscriptionContent.ValidateUrl(url);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !ConfirmVpnRisk(
                "Подписка открывается по http без шифрования: провайдер и ТСПУ увидят ключи всех серверов из неё.\n\n" +
                "Если панель 3x-ui это позволяет, включите для подписки https. Всё равно продолжить?"))
        {
            return "Подписка не сохранена.";
        }

        SubscriptionFetch? fetched = null;
        string? downloadError = null;
        await Serialized("Загрузка подписки…", silentErrors: true, async () =>
        {
            try
            {
                fetched = await FetchSubscriptionAsync(uri);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                Log.Error("Subscription download failed", ex);
                downloadError = ex is InvalidDataException ? ex.Message : "Не удалось скачать подписку: " + ex.Message;
            }
        });
        if (fetched is null) return downloadError ?? "Не удалось скачать подписку, подробности в логе.";

        var content = fetched.Content;
        var insecure = content.Servers.Count(s => s.Server.Insecure);
        var allowInsecure = insecure > 0 && ConfirmVpnRisk(InsecureWarning(insecure));
        if (insecure == content.Servers.Count && !allowInsecure) return "Подписка не сохранена: в ней только серверы без проверки сертификата.";

        var source = new VpnSource { SubscriptionUrl = uri.ToString(), AllowInsecure = allowInsecure };
        Fill(source, fetched);
        return await SaveSourceAsync(source, "Подключение подписки…");
    }

    private static void Fill(VpnSource source, SubscriptionFetch fetched)
    {
        source.Links = fetched.Content.Links.ToList();
        source.Title = fetched.Title ?? source.Title;
        source.Used = fetched.Usage?.Used;
        source.Total = fetched.Usage is { Total: > 0 } u ? u.Total : null;
        source.Expire = fetched.Usage?.Expire;
        source.UpdateHours = fetched.Interval.TotalHours;
        source.SkippedCount = fetched.Content.Skipped.Count;
        source.FetchedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Downloads and parses the subscription (the URL is never logged: it is a credential).</summary>
    private Task<SubscriptionFetch> FetchSubscriptionAsync(Uri uri) => DownloadThroughAnyPathAsync(async (client, ct) =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Panels pick the output format by client name; this asks for the plain list of share links.
        request.Headers.UserAgent.ParseAdd("v2rayN/7.0");
        request.Headers.UserAgent.ParseAdd("ZapretHub/" + (typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "0"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxSubscriptionSize) throw new InvalidDataException("Подписка слишком большая.");
        string? Header(string name) => response.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxSubscriptionSize + 1];
        var read = await reader.ReadBlockAsync(buffer, ct);
        if (read > MaxSubscriptionSize) throw new InvalidDataException("Подписка слишком большая.");

        var content = SubscriptionContent.Parse(new string(buffer, 0, read));
        // An empty result may be a block page: InvalidDataException lets the download retry through the tunnel.
        if (content.Servers.Count == 0)
        {
            throw new InvalidDataException("В подписке нет поддерживаемых серверов." +
                                           (content.Skipped.Count > 0 ? " Пропущено: " + string.Join("; ", content.Skipped.Take(3)) : ""));
        }
        return new SubscriptionFetch(
            content,
            SubscriptionContent.ParseTitle(Header("profile-title")),
            SubscriptionUsage.Parse(Header("subscription-userinfo")),
            SubscriptionContent.ParseUpdateInterval(Header("profile-update-interval")));
    });

    /// <summary>Re-downloads the subscription. A failed or empty download keeps the previous list.</summary>
    public Task RefreshSubscriptionAsync(bool interactive) =>
        Serialized("Обновление подписки…", silentErrors: !interactive, async () =>
        {
            _lastSubscriptionAttempt = DateTime.UtcNow;
            var source = VpnSourceStore.Load();
            if (source?.SubscriptionUrl is not { } url) return;

            var fetched = await FetchSubscriptionAsync(new Uri(url));
            var usable = fetched.Content.Servers.Count(s => !s.Server.Insecure || source.AllowInsecure);
            if (usable == 0) throw new InvalidOperationException("В новой версии подписки нет подходящих серверов, оставлен прежний список.");
            var before = source.Signature();
            Fill(source, fetched);
            VpnSourceStore.Save(source);
            UseSource(source);
            if (source.Signature() != before)
            {
                _pings.Clear();
                await ApplyVpnCoreAsync(interactive, needProbe: false);
            }
            if (interactive) Notify?.Invoke("Подписка обновлена", source.Describe(), ToolTipIcon.Info);
        });

    // ---------- choosing the server ----------

    /// <summary>A manual choice: pins this server and turns automatic choice off.</summary>
    public Task SelectVpnServerAsync(string tag) => Serialized("Смена сервера VPS…", silentErrors: false, async () =>
    {
        if (_servers.All(s => s.Tag != tag)) return;
        Settings.VpnSelectedServer = tag;
        Settings.VpnAutoBest = false;
        _settingsStore.Save(Settings);
        await SwitchRunningServerAsync(tag);
    });

    public Task SetVpnAutoBestAsync(bool on) => Serialized("Настройка VPS…", silentErrors: false, () =>
    {
        Settings.VpnAutoBest = on;
        _settingsStore.Save(Settings);
        if (on) _nextBestCheck = DateTime.UtcNow;
        return Task.CompletedTask;
    });

    /// <summary>Switches the live selector; new connections use the server right away, running ones stay.</summary>
    private async Task SwitchRunningServerAsync(string tag)
    {
        if (_clash is not { } clash || !IsVpnRunning) return;
        await clash.SelectAsync(SingBoxConfig.ProxyTag, tag, CancellationToken.None);
        if (_appliedLaunch is { } launch) _appliedLaunch = launch with { ActiveTag = tag };
        Log.Info($"VPS server switched to {Describe(tag)}");
    }

    private string Describe(string tag) => _servers.FirstOrDefault(s => s.Tag == tag)?.Server.ToString() ?? tag;

    /// <summary>User-started check of all servers (starts sing-box for the check when it is not running).</summary>
    public async Task PingServersManuallyAsync()
    {
        IReadOnlyList<ServerPing>? result = null;
        await Serialized("Проверка серверов…", silentErrors: false, async () =>
        {
            if (_servers.Count == 0) throw new InvalidOperationException("Сначала добавьте сервер или подписку.");
            _autoPingCts?.Cancel();
            try
            {
                await ApplyVpnCoreAsync(interactive: true, needProbe: true);
                if (_clash is not { } clash) throw new InvalidOperationException("sing-box не запущен.");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                result = await PingAllAsync(clash, cts.Token);
                if (Settings.VpnAutoBest && ServerRanker.ChooseBetter(ActiveServerTag, result) is { } better)
                {
                    await SwitchToBetterAsync(better, result);
                }
            }
            finally
            {
                try
                {
                    await ApplyVpnCoreAsync(interactive: false, needProbe: false);
                }
                catch (Exception cleanupEx)
                {
                    Log.Error("Restoring VPS state after server check failed", cleanupEx);
                }
            }
        });
    }

    /// <summary>Pings every server. A server that misses its first sample is not sampled again (a dead server
    /// costs one timeout, not three). Results are kept only for a completed round.</summary>
    private async Task<IReadOnlyList<ServerPing>> PingAllAsync(ClashApiClient clash, CancellationToken ct)
    {
        Interlocked.Increment(ref _pinging);
        Changed();
        try
        {
            var servers = _servers.ToList();
            using var gate = new SemaphoreSlim(PingParallel);
            var tasks = servers.Select(async s =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var samples = new List<int?>();
                    for (var i = 0; i < PingSamples; i++)
                    {
                        var d = await clash.DelayAsync(s.Tag, PingTimeout, ct);
                        samples.Add(d);
                        if (d is null && i == 0) break;
                    }
                    return ServerPing.From(s.Tag, samples);
                }
                finally
                {
                    gate.Release();
                }
            });
            var result = await Task.WhenAll(tasks);
            ct.ThrowIfCancellationRequested();
            foreach (var p in result) _pings[p.Tag] = p;
            LastPingTime = DateTime.Now;
            Log.Info("Server ping: " + string.Join(", ", result.Select(p => $"{Describe(p.Tag)}={(p.MedianMs is { } ms ? $"{ms}ms" : "down")}")));
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _pinging);
            Changed();
        }
    }

    private async Task SwitchToBetterAsync(string better, IReadOnlyList<ServerPing> pings)
    {
        var old = ActiveServerTag;
        Settings.VpnSelectedServer = better;
        _settingsStore.Save(Settings);
        await SwitchRunningServerAsync(better);
        var oldMs = pings.FirstOrDefault(p => p.Tag == old)?.MedianMs;
        var newMs = pings.First(p => p.Tag == better).MedianMs;
        var name = _servers.First(s => s.Tag == better).Server.Name ?? _servers.First(s => s.Tag == better).Server.Host;
        Notify?.Invoke("Сервер VPS сменён", $"{name}: {newMs} мс" + (oldMs is { } o ? $" вместо {o} мс" : " (прежний не отвечал)"), ToolTipIcon.Info);
    }

    // ---------- background upkeep (game watch tick, UI thread) ----------

    /// <summary>
    /// Periodic work that must not disturb a game: subscription refresh, rule-set refresh and switching to a
    /// faster server. Only called while none of the game profiles' processes is running.
    /// </summary>
    private async Task VpnUpkeepAsync()
    {
        var now = DateTime.UtcNow;
        if (_source?.UpdateDue(DateTimeOffset.UtcNow) == true && now - _lastSubscriptionAttempt > SubscriptionRetry)
        {
            await RefreshSubscriptionAsync(interactive: false);
        }

        var plan = CurrentPlan();
        var files = plan.ActiveCategories.Select(RuleCatalog.Find).Where(c => c is not null).SelectMany(c => c!.Files).ToList();
        var due = _rulesMissing ? RuleRetryMissing : RuleRefreshCheck;
        if (IsVpnRunning && files.Any(RuleSetStore.IsStale) && now - _lastRuleAttempt > due)
        {
            _lastRuleAttempt = now;
            await ApplyVpnAsync(interactive: false, refreshRules: true);
        }

        if (Settings.VpnAutoBest && _servers.Count > 1 && IsVpnRunning && _clash is { } clash && now >= _nextBestCheck
            && BusyText is null && _autoPing is null)
        {
            _nextBestCheck = now + AutoBestInterval;
            // Not awaited: the game watch keeps ticking (and cancels this round the moment a game starts).
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            _autoPingCts = cts;
            _autoPing = AutoBestRoundAsync(clash, cts);
        }
    }

    private async Task AutoBestRoundAsync(ClashApiClient clash, CancellationTokenSource cts)
    {
        try
        {
            var pings = await PingAllAsync(clash, cts.Token);
            if (ServerRanker.ChooseBetter(ActiveServerTag, pings) is { } better && !cts.IsCancellationRequested)
            {
                await Serialized("Смена сервера VPS…", silentErrors: true, async () =>
                {
                    // The tunnel may have restarted meanwhile: its new selector already starts on the saved choice.
                    if (_clash != clash || GameRunning) return;
                    await SwitchToBetterAsync(better, pings);
                });
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Server check cancelled (a game started or the tunnel restarted)");
        }
        catch (Exception ex)
        {
            Log.Error("Automatic server check failed", ex);
        }
        finally
        {
            _autoPing = null;
            // Not disposed: another thread may still call Cancel on it; it only holds a finished timer.
            if (ReferenceEquals(_autoPingCts, cts)) _autoPingCts = null;
        }
    }
}
