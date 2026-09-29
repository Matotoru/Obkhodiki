using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;
using Obkhodiki.Core.Diagnostics;
using Obkhodiki.Core.Engine;
using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Testing;

namespace Obkhodiki.App;

/// <summary>
/// One-click diagnostics: a zip with the state, a live check of the target sites (as the computer reaches them
/// now, and through the VPS) and the recent logs — everything run through <see cref="Redactor"/>, and no VPS
/// subscription or server links, so the user can send it to someone who helps.
/// </summary>
internal sealed partial class AppController
{
    private const int LogTailLines = 3000;

    public Task BuildDiagnosticsAsync(string zipPath, IProgress<string>? progress) => Serialized("Сбор отчёта…", silentErrors: false, async () =>
    {
        progress?.Report("Состояние программы…");
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);

        Line($"Obkhodiki {SelfUpdate.CurrentVersion}, отчёт от {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss zzz}");
        Line($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit, .NET {Environment.Version}");
        Line($"Запуск из: {AppContext.BaseDirectory}");
        Line();
        Line("== Обход ==");
        Line($"Flowseal: {_engine?.Version ?? "не установлен"}; работает: {(_runner.IsRunning ? "да" : "нет")}");
        Line($"Стратегия: {ActiveStrategyName}; игровой фильтр: {Settings.GameFilter}");
        var conflicts = ConflictDetector.Find(ConflictDetector.SnapshotCandidates(), RunningServices(), Environment.ProcessId);
        Line($"Другие обходы: {(conflicts.Any ? string.Join(", ", conflicts.Processes.Select(p => p.Name).Concat(conflicts.Services)) : "не найдены")}");
        Line();
        Line("== Игры ==");
        foreach (var g in Settings.GameProfiles)
        {
            Line($"- {g.Name} ({g.ProcessName ?? "без exe"}): {(g.Enabled ? "вкл" : "выкл")}, маршрут {g.Route}, TCP {g.TcpPorts}, UDP {g.UdpPorts}, адресов для замера {g.ProbeEndpoints.Count}");
        }
        if (Settings.GameProfiles.Count == 0) Line("нет");
        Line();
        Line("== VPS ==");
        Line($"sing-box: {SingBoxVersion ?? "не установлен"}; amnezia-box: {AmneziaBoxVersion ?? "нет"}; включён: {Settings.VpnEnabled}; работает: {IsVpnRunning}; весь трафик: {Settings.VpnFullTunnel}");
        Line($"Источник: {VpnServerName ?? "не задан"}");
        foreach (var s in VpnServers)
        {
            var ping = ServerPings.TryGetValue(s.Tag, out var p) ? p.MedianMs is { } ms ? $"{ms} мс" : "нет ответа" : "не проверен";
            Line($"- {s.Server.Protocol} {s.Server.Host}:{s.Server.Port}{(s.Tag == ActiveServerTag ? " (активный)" : "")}: {ping}");
        }
        Line($"Категории: через VPS [{string.Join(", ", Settings.VpnProxyCategories)}], напрямую [{string.Join(", ", Settings.VpnDirectCategories)}]");
        Line($"Программы через VPS: {Settings.VpnProcesses.Count}, сайтов: {Settings.VpnDomains.Count}; всегда напрямую: программ {Settings.VpnBypassProcesses.Count}, адресов {Settings.VpnBypassEntries.Count}");
        Line();
        Line("== Telegram ==");
        Line($"TG WS Proxy: {TgVersion ?? "не установлен"}; включён: {Settings.TelegramEnabled}; работает: {IsTgRunning}");
        Line();
        Line("== Сеть ==");
        Line($"IPv6 в интернет: {(HostHasIpv6() ? "есть" : "нет")}");
        Line($"Системный прокси: {SystemProxyDescription()}");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var props = nic.GetIPProperties();
            var dns = string.Join(", ", props.DnsAddresses.Select(a => a.ToString()));
            Line($"- {nic.Name} ({nic.Description}): DNS {(dns.Length > 0 ? dns : "—")}");
        }
        Line();

        progress?.Report("Проверка сайтов…");
        Line("== Проверка сайтов из списка целей ==");
        Line("«Сейчас» — как компьютер открывает сайт в текущем состоянии (с обходом, если он включён); «через VPS» — через активный сервер.");
        await CheckTargetsAsync(Line);
        Line();

        progress?.Report("Логи…");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var privateHosts = await PrivateHostsAsync();
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(Redactor.Redact(text, privateHosts));
            }
            Add("report.txt", report.ToString());
            Add("settings.json", SettingsBundle.Create(Settings, new Dictionary<string, string>(), new Dictionary<string, string>(), null,
                SelfUpdate.CurrentVersion, DateTimeOffset.Now).Serialize());
            Add("app-log.txt", TailOf(AppPaths.Log));
            Add("sing-box-log.txt", TailOf(AppPaths.SingBoxLog));
        }
        Log.Info($"Diagnostics report saved to {zipPath}");
    });

    private async Task CheckTargetsAsync(Action<string> line)
    {
        SeedTargets();
        var targets = TargetsFile.Parse(await File.ReadAllTextAsync(AppPaths.Targets));
        using var nowHttp = new HttpClient(HttpConnectivityProbe.CreateNonPoolingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var nowProbe = new HttpConnectivityProbe(nowHttp, TimeSpan.FromSeconds(8));
        HttpClient? vpnHttp = null;
        if (IsVpnRunning && _probeAuth is { } auth)
        {
            vpnHttp = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{_probePort}") { Credentials = new NetworkCredential(auth.User, auth.Password) },
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.Zero,
            }) { Timeout = Timeout.InfiniteTimeSpan };
        }
        using (vpnHttp)
        {
            var vpnProbe = vpnHttp is null ? null : new HttpConnectivityProbe(vpnHttp, TimeSpan.FromSeconds(10));
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(targets.Select(async t =>
            {
                await gate.WaitAsync();
                try
                {
                    var now = await nowProbe.ProbeAsync(t, CancellationToken.None);
                    var viaVpn = vpnProbe is null ? null : await vpnProbe.ProbeAsync(t, CancellationToken.None);
                    return (t, now, viaVpn);
                }
                finally
                {
                    gate.Release();
                }
            }));
            static string Show(ProbeResult r) => r.Ok ? $"открылся за {r.Latency.TotalMilliseconds:F0} мс" : $"НЕ открылся ({r.Latency.TotalSeconds:F1} с)";
            foreach (var (t, now, viaVpn) in results)
            {
                line($"- {t.Name} ({t.Url.Host}): сейчас {Show(now)}" + (viaVpn is null ? "" : $"; через VPS {Show(viaVpn)}"));
            }
            if (vpnProbe is null) line("(VPS не запущен — проверка через VPS пропущена)");
        }
    }

    /// <summary>
    /// The user's VPS servers (current and saved subscriptions) and subscription hosts, by name and by address —
    /// sing-box logs show the resolved IP, not the name.
    /// </summary>
    private async Task<IReadOnlyList<string>> PrivateHostsAsync()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { VpnSourceStore.Load() }.Concat(VpnSourceStore.LoadSaved()))
        {
            if (source is null) continue;
            if (source.SubscriptionHost is { } sub) hosts.Add(sub);
            foreach (var s in source.Servers()) hosts.Add(s.Server.Host);
        }
        var names = hosts.Where(h => !IPAddress.TryParse(h, out _)).ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var name in names)
        {
            try
            {
                foreach (var ip in await Dns.GetHostAddressesAsync(name, cts.Token)) hosts.Add(ip.ToString());
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
            {
            }
        }
        return hosts.ToList();
    }

    private static IEnumerable<string> RunningServices()
    {
        foreach (var name in ConflictDetector.ServiceNames)
        {
            bool running;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(name);
                running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (running) yield return name;
        }
    }

    private static string SystemProxyDescription()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable") is int e && e != 0;
            var server = key?.GetValue("ProxyServer") as string;
            var pac = key?.GetValue("AutoConfigURL") as string;
            var env = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("ALL_PROXY");
            return $"{(enabled ? "включён" : "выключен")}{(server is { Length: > 0 } ? $" ({server})" : "")}" +
                   (pac is { Length: > 0 } ? $", PAC {pac}" : "") + (env is not null ? $", переменная окружения {env}" : "") +
                   " — Obkhodiki его не использует";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return "не удалось прочитать";
        }
    }

    private static string TailOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return "(нет файла)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } l)
            {
                lines.Enqueue(l);
                if (lines.Count > LogTailLines) lines.Dequeue();
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(не удалось прочитать: " + ex.Message + ")";
        }
    }
}
