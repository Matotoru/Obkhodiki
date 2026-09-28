using Obkhodiki.Core.Games;
using Obkhodiki.Core.Settings;

namespace Obkhodiki.Core.Vpn;

/// <summary>What the tunnel has to carry right now, derived from settings and the per-session auto decisions.</summary>
/// <param name="Processes">Through the VPS.</param>
/// <param name="DirectProcesses">Kept off the VPS in full-tunnel mode (games set to direct, this app).</param>
/// <param name="BypassProcesses">The user's "never through the VPS" programs, in either mode.</param>
/// <param name="BypassEntries">The user's "never through the VPS" domains and IP/CIDR addresses, in either mode.</param>
public sealed record VpnPlan(
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Domains,
    bool FullTunnel,
    IReadOnlyList<string> DirectProcesses,
    IReadOnlyList<string> ProxyCategories,
    IReadOnlyList<string> DirectCategories)
{
    public IReadOnlyList<string> BypassProcesses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BypassEntries { get; init; } = Array.Empty<string>();

    /// <summary>The app's own traffic (downloads, direct-path measurements) never rides the tunnel.</summary>
    public const string SelfProcess = "Obkhodiki.exe";

    public VpnPlan(IReadOnlyList<string> processes, IReadOnlyList<string> domains)
        : this(processes, domains, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
    {
    }

    public bool NeedsTunnel => FullTunnel || Processes.Count > 0 || Domains.Count > 0 || ProxyCategories.Count > 0;

    /// <summary>Rule-set categories the active mode uses.</summary>
    public IEnumerable<string> ActiveCategories => FullTunnel ? DirectCategories : ProxyCategories;

    /// <param name="autoVpnProcesses">Auto games a measurement sent through the VPS this session.</param>
    /// <param name="autoDecidedProcesses">Auto games measured this session (either way).</param>
    public static VpnPlan From(AppSettings settings, IReadOnlyCollection<string> autoVpnProcesses,
        IReadOnlyCollection<string>? autoDecidedProcesses = null)
    {
        var processes = new List<string>(settings.VpnProcesses);
        var direct = new List<string> { SelfProcess };
        // Auto profiles only after a measurement chose the VPS, and only while the profile is still an enabled
        // Auto profile for that exe (a late or stale decision must not keep traffic on the VPS).
        var autoExes = settings.GameProfiles
            .Where(p => p.Enabled && p.Route == GameRoute.Auto && p.ProcessName is not null)
            .Select(p => p.ProcessName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decided = (autoDecidedProcesses ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in settings.GameProfiles)
        {
            if (!p.Enabled || p.ProcessName is not { } exe) continue;
            switch (p.Route)
            {
                case GameRoute.Vpn:
                    processes.Add(exe);
                    break;
                case GameRoute.Direct:
                    direct.Add(exe);
                    break;
                case GameRoute.Auto when decided.Contains(exe) && !autoVpnProcesses.Contains(exe):
                    direct.Add(exe);
                    break;
            }
        }
        processes.AddRange(autoVpnProcesses.Where(autoExes.Contains));

        // "Never through the VPS" beats every way a program can end up there (lists, game routes, Auto).
        var bypass = settings.VpnBypassProcesses.Where(SingBoxConfig.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var proxy = processes.Where(SingBoxConfig.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !bypass.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        return new VpnPlan(
            proxy,
            settings.VpnDomains.ToList(),
            settings.VpnFullTunnel,
            direct.Where(SingBoxConfig.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(d => !proxy.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList(),
            RuleCatalog.Sanitize(settings.VpnProxyCategories, RuleCatalog.Proxy),
            RuleCatalog.Sanitize(settings.VpnDirectCategories, RuleCatalog.Direct))
        {
            BypassProcesses = bypass,
            BypassEntries = settings.VpnBypassEntries.Select(SingBoxConfig.NormalizeBypassEntry).Where(e => e is not null).Select(e => e!).Distinct().ToList(),
        };
    }
}
