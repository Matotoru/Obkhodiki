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

    public sealed record Credit(string Project, string Author, string License, string Role, string Url);

    /// <summary>Projects Obkhodiki is built on (same list as THIRD-PARTY-NOTICES.md).</summary>
    public static IReadOnlyList<Credit> Credits { get; } = new[]
    {
        new Credit("zapret", "bol-van", "MIT", "движок обхода DPI (winws)", "https://github.com/bol-van/zapret"),
        new Credit("zapret-discord-youtube", "Flowseal", "MIT", "стратегии и сборка zapret для Windows", "https://github.com/Flowseal/zapret-discord-youtube"),
        new Credit("WinDivert", "basil00", "LGPL-3.0 / GPL-2.0", "драйвер перехвата пакетов", "https://github.com/basil00/WinDivert"),
        new Credit("tg-ws-proxy", "Flowseal", "MIT", "прокси для Telegram", "https://github.com/Flowseal/tg-ws-proxy"),
        new Credit("sing-box", "nekohasekai (SagerNet)", "GPL-3.0", "туннель к VPS", "https://github.com/SagerNet/sing-box"),
        new Credit("sing-geosite / sing-geoip", "nekohasekai (SagerNet)", "GPL-3.0", "наборы правил маршрутизации", "https://github.com/SagerNet/sing-geosite"),
        new Credit("russia-v2ray-rules-dat", "runetfreedom", "GPL-3.0", "список заблокированного в России", "https://github.com/runetfreedom/russia-v2ray-rules-dat"),
        new Credit("WPF UI", "Leszek Pomianowski и участники", "MIT", "оформление окна", "https://github.com/lepoco/wpfui"),
        new Credit(".NET Community Toolkit", ".NET Foundation", "MIT", "MVVM", "https://github.com/CommunityToolkit/dotnet"),
        new Credit(".NET", ".NET Foundation", "MIT", "платформа", "https://github.com/dotnet/runtime"),
    };

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
        new Entry("0.9.0", new[]
        {
            "VPS можно подключать не только туннелем, но и как прокси: «VPS» → «Способ подключения» → «Прокси». Obkhodiki прописывает себя в системный прокси Windows (127.0.0.1:8780, HTTP и SOCKS5), через VPS идут браузеры и программы, которые его используют; игры и UDP — напрямую. При выключении прежние настройки прокси возвращаются.",
            "Поддержка AmneziaVPN из версий 0.8 (ключи vpn:// и конфиги AmneziaWG) убрана. Скачанный для неё amnezia-box удаляется сам.",
        }),
        new Entry("0.7.0", new[]
        {
            "VMess: ссылки vmess:// и серверы VMess в подписках (в том числе JSON-подписках) теперь принимаются.",
            "CS2, Dota 2, Deadlock и WARDOGS получают точный список ретрансляторов Valve прямо из Steam, а не всю сеть Valve целиком; раз в неделю список обновляется сам.",
            "Профилем игры можно поделиться: «…» → «Поделиться…» сохраняет игру с её адресами в файл, а кнопка «Из файла» на странице «Игры» его загружает. Удобно для игр, где нужна запись матча: записал один — пользуются все.",
        }),
        new Entry("0.6.1", new[]
        {
            "Быстрый автоподбор: сначала проверяются стратегии, которые уже работали у вас, и подбор останавливается на первой, открывшей все сайты. Полный прогон — кнопка «Проверить все».",
            "Если антивирус удалил или заблокировал winws или WinDivert, программа так и говорит и подсказывает, как вернуть файлы, вместо непонятной ошибки.",
            "Отчёт для диагностики больше не содержит адресов ваших VPS-серверов (вместо них метки вида srv-1a2b) и имени пользователя Windows в путях.",
        }),
        new Entry("0.6.0", new[]
        {
            "Популярные игры в один клик: WARDOGS, CS2, Dota 2, Deadlock, VALORANT и League of Legends работают сразу — адреса серверов программа берёт из открытых реестров сетей. Apex, PUBG, Fortnite, Rust, Marvel Rivals и Tarkov добавляются так же, а их адреса записываются за один матч.",
            "Карточки игр с обложками из Steam (для остальных — в фирменных цветах игры); на любую карточку можно поставить свою картинку.",
            "Мастер первого запуска: стратегия, игры, VPS и оформление по шагам. Пройти заново — в «Настройках».",
            "Светлая тема и восемь цветовых палитр, у каждой свой оттенок для светлой и тёмной темы («Настройки» → «Оформление»).",
            "VPS: список «Всегда напрямую» — программы, сайты и адреса, которые никогда не идут через VPS (например, банк).",
            "VPS: предупреждения, когда у подписки кончается трафик или срок, и счётчик трафика через VPS.",
            "Настройки можно сохранить в файл и загрузить на другом компьютере (без подписок и паролей).",
            "Кнопка «Собрать отчёт»: один zip с проверкой сайтов, состоянием и логами — пароли и ключи вычищаются.",
        }),
        new Entry("0.5.3", new[]
        {
            "Автоподбор больше не «проваливает» сайт из-за одного недоступного IP: если первый адрес сайта не отвечает, проверка, как и браузер, через долю секунды пробует следующий. Раньше так ложно не открывался, например, updates.discord.com.",
        }),
        new Entry("0.5.2", new[]
        {
            "Автоподбор стратегии показывает, какие именно сайты из списка целей не открылись: под каждой частично рабочей стратегией, во всплывающей подсказке и в логе.",
        }),
        new Entry("0.5.1", new[]
        {
            "Проверка и скачивание обновлений больше не ломаются из-за прокси, который оставила в Windows другая VPN-программа (например, 127.0.0.1:2080): Obkhodiki ходит в интернет своими путями и системный прокси не использует.",
        }),
        new Entry("0.5.0", new[]
        {
            "Программа переехала на .NET 10 и теперь одна: .NET встроен внутрь, отдельно ничего ставить не нужно. Поддержка .NET 8, на котором она работала раньше, заканчивается 10 ноября 2026 года.",
            "Файлы прежней версии в папке программы убираются сами после успешного обновления.",
        }),
        new Entry("0.4.7", new[]
        {
            "Hysteria2 с прыжками по портам (port hopping): ссылки вида host:443,20000-30000 и с параметром mport теперь принимаются — блокировка одного порта больше не обрывает VPS.",
            "Замер качества надёжнее: больше попыток, и одна случайная потеря больше не решает, пойдёт игра через VPS или напрямую.",
            "Режим «Авто» для игр честно говорит, когда не смог замерить (например, игра только на UDP), и советует выбрать маршрут вручную.",
            "У проекта появилась лицензия MIT.",
        }),
        new Entry("0.4.6", new[]
        {
            "Несколько подписок VPS: при добавлении новой прежняя не удаляется, а попадает в «Сохранённые подписки» на странице VPS. Переключиться обратно — одной кнопкой, выбранный в подписке сервер запоминается.",
            "«Качество канала» точнее для любой игры: замер понимает и старые серверы на TLS 1.2 (например, сервер War Dogs раньше не отвечал — отсюда были ложные 33% потерь), пробует любые порты, сам отбирает отвечающие адреса и не подмешивает CDN вроде Cloudflare, которые стоят рядом с вами, а не с игрой. Джиттер считается по каждому адресу отдельно.",
        }),
        new Entry("0.4.5", new[]
        {
            "У программы появился свой значок — котик: на файле программы, в панели задач, в заголовке окна и в трее (с цветной точкой состояния). В окне он едва заметно выглядывает из-за страниц.",
        }),
        new Entry("0.4.4", new[]
        {
            "VPS больше не ломает сайты без IPv6-интернета: если у провайдера нет IPv6, туннель работает только по IPv4. Раньше при включённом VPS не открывались Яндекс, Google и другие сайты с IPv6-адресами, а с категорией «Нейросети» — Claude и ChatGPT.",
            "Пинг серверов VPS теперь честный: замеряется один круг до сервера, а не целый запрос к сайту. Раньше число было завышено в 2–3 раза (например, 88 мс вместо 37).",
        }),
        new Entry("0.4.3", new[]
        {
            "Исправлено самообновление: после установки новая версия принимала установщик за уже запущенную программу, и обновление откатывалось.",
        }),
        new Entry("0.4.2", new[]
        {
            "Галочки категорий VPS (Instagram, нейросети, YouTube…) применяются сразу — кнопка «Сохранить» нужна только для программ и сайтов.",
            "«Качество канала»: всегда можно измерить общий канал до Cloudflare и Google, а для игры без адресов появляется подсказка, как их записать.",
            "В «Настройках» → «Диагностика» — «Открыть лог VPS» с журналом sing-box.",
        }),
        new Entry("0.4.1", new[]
        {
            "Подписки с ограничением по устройствам (BuzzVPN и другие панели Remnawave) теперь добавляются: программа сообщает панели постоянный идентификатор этого компьютера.",
            "Подписки в формате JSON (для Happ, v2RayTun, Incy) разбираются: серверы VLESS, Trojan и Shadowsocks из них попадают в список с их названиями.",
            "В «Настройках» — раздел «Авторы и благодарности» со ссылками на проекты, на которых построен Obkhodiki.",
        }),
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
