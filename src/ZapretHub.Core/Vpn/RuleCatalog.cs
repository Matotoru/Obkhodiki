namespace ZapretHub.Core.Vpn;

/// <summary>A sing-box binary rule-set file (.srs) downloaded by the app and fed to sing-box as a local file.</summary>
public sealed record RuleSetFile(string Tag, string Url)
{
    public const long MaxSize = 16 * 1024 * 1024;

    /// <summary>Binary rule-sets start with "SRS" and a version byte.</summary>
    public static bool LooksValid(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data.Length <= MaxSize && data[0] == (byte)'S' && data[1] == (byte)'R' && data[2] == (byte)'S';

    /// <summary>geosite sets hold domains (also usable for DNS); geoip sets hold only addresses.</summary>
    public bool HasDomains => Tag.StartsWith("geosite-", StringComparison.Ordinal);
}

/// <summary>A category the user can tick: a named group of rule-sets.</summary>
public sealed record RuleCategory(string Id, string Title, string? Description, IReadOnlyList<RuleSetFile> Files);

/// <summary>Built-in categories. Sources: SagerNet sing-geosite/sing-geoip and runetfreedom/russia-v2ray-rules-dat.</summary>
public static class RuleCatalog
{
    private const string SagerSite = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/";
    private const string SagerIp = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/";
    private const string RunetSite = "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/sing-box/rule-set-geosite/";
    private const string RunetIp = "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/sing-box/rule-set-geoip/";

    private static RuleSetFile Site(string name, string root = SagerSite) => new($"geosite-{name}", $"{root}geosite-{name}.srs");
    private static RuleSetFile Ip(string name, string root = SagerIp) => new($"geoip-{name}", $"{root}geoip-{name}.srs");

    /// <summary>Kept direct when all traffic goes through the VPS.</summary>
    public static IReadOnlyList<RuleCategory> Direct { get; } = new[]
    {
        new RuleCategory("ru", "Российские сайты и адреса", "category-ru и geoip-ru: банки, госуслуги, маркетплейсы работают как обычно",
            new[] { Site("category-ru"), Ip("ru") }),
    };

    /// <summary>Sent through the VPS in selective mode.</summary>
    public static IReadOnlyList<RuleCategory> Proxy { get; } = new[]
    {
        new RuleCategory("ru-blocked", "Заблокированное в России", "Список runetfreedom: сайты и адреса, закрытые РКН",
            new[] { Site("ru-blocked", RunetSite), Ip("ru-blocked", RunetIp) }),
        new RuleCategory("ai", "Нейросети", "ChatGPT, Claude, Gemini",
            new[] { Site("openai"), Site("anthropic"), Site("google-gemini") }),
        new RuleCategory("youtube", "YouTube", "Обычно работает и через zapret; через VPS стабильнее качество",
            new[] { Site("youtube") }),
        new RuleCategory("discord", "Discord", "Обычно работает и через zapret",
            new[] { Site("discord") }),
        new RuleCategory("telegram", "Telegram", "Вместо или вместе с TG WS Proxy",
            new[] { Site("telegram"), Ip("telegram", RunetIp) }),
        new RuleCategory("meta", "Instagram и Facebook", null,
            new[] { Site("instagram"), Site("facebook") }),
        new RuleCategory("twitter", "X (Twitter)", null,
            new[] { Site("twitter") }),
    };

    public static RuleCategory? Find(string id) => Direct.Concat(Proxy).FirstOrDefault(c => c.Id == id);

    /// <summary>Known ids only, in catalog order, without duplicates.</summary>
    public static List<string> Sanitize(IEnumerable<string>? ids, IReadOnlyList<RuleCategory> from)
    {
        var set = (ids ?? Array.Empty<string>()).ToHashSet();
        return from.Where(c => set.Contains(c.Id)).Select(c => c.Id).ToList();
    }
}
