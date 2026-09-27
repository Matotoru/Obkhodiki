using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Updates;

/// <summary>Where the app itself is published.</summary>
public static class AppInfo
{
    public const string Name = "Obkhodiki";

    /// <summary>GitHub repository with the app's releases ("owner/repo"). Releases must be public to be downloadable.</summary>
    public const string Repository = "Matotoru/Obkhodiki";

    public const string AuthorName = "matotoru";
    public const string AuthorUrl = "https://github.com/Matotoru";

    /// <summary>Release asset name the CI workflow produces.</summary>
    public static string AssetName(string version) => $"{Name}-{version}-win-x64.zip";
}

/// <summary>Releases of the app itself (same verified path as Flowseal: pinned repo, mandatory SHA-256 digest).</summary>
public sealed partial class AppReleaseClient : GitHubReleaseSource
{
    public const long MaxDownloadBytes = 100L * 1024 * 1024;

    [GeneratedRegex(@"^Obkhodiki-[0-9]+(\.[0-9]+){1,3}-win-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex Asset();

    public AppReleaseClient(HttpClient http, TimeSpan? downloadDeadline = null)
        : base(http, AppInfo.Repository, Asset(), MaxDownloadBytes, downloadDeadline)
    {
    }
}

/// <summary>What changed in each version, shown once after an update.</summary>
public static class Changelog
{
    public sealed record Entry(string Version, IReadOnlyList<string> Items);

    /// <summary>Newest first. Add an entry with every release.</summary>
    public static IReadOnlyList<Entry> Entries { get; } = new[]
    {
        new Entry("0.4.0", new[]
        {
            "Новое имя: Obkhodiki. Настройки, серверы и игры перенесены автоматически.",
            "Программа обновляет сама себя: проверяет новые версии на GitHub и ставит их после вашего подтверждения. Обновлённая версия ставится в Program Files и появляется в меню «Пуск».",
            "Это окно: после каждого обновления видно, что добавилось.",
            "Версия программы и ссылка на автора — внизу боковой панели.",
        }),
        new Entry("0.3.1", new[]
        {
            "Главный выключатель VPS: полностью останавливает sing-box, настройки сохраняются.",
        }),
        new Entry("0.3.0", new[]
        {
            "Подписки из 3x-ui и серверы VLESS (Reality, ws, gRPC), Trojan, Shadowsocks.",
            "Режим «Весь трафик через VPS» — российские сайты и игры с прямым маршрутом идут мимо.",
            "Категории для выборочного режима: заблокированное в России, нейросети, YouTube, Discord, Telegram.",
            "Автовыбор самого быстрого сервера раз в 5 минут, не во время игр.",
        }),
        new Entry("0.2.0", new[]
        {
            "Новое окно в стиле Windows 11 вместо перегруженного меню в трее.",
        }),
    };

    /// <summary>Entries newer than <paramref name="lastSeen"/> up to and including <paramref name="current"/>.</summary>
    public static IReadOnlyList<Entry> Since(string? lastSeen, string current) =>
        Entries.Where(e => !ReleaseVersion.IsNewer(e.Version, current)
                           && (lastSeen is null || ReleaseVersion.IsNewer(e.Version, lastSeen)))
            .ToList();
}
