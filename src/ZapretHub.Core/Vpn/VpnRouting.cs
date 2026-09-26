using ZapretHub.Core.Games;
using ZapretHub.Core.Settings;

namespace ZapretHub.Core.Vpn;

/// <summary>What the tunnel has to carry right now, derived from settings and the per-session auto decisions.</summary>
public sealed record VpnPlan(IReadOnlyList<string> Processes, IReadOnlyList<string> Domains)
{
    public bool NeedsTunnel => Processes.Count > 0 || Domains.Count > 0;

    public static VpnPlan From(AppSettings settings, IReadOnlyCollection<string> autoVpnProcesses)
    {
        var processes = new List<string>(settings.VpnProcesses);
        foreach (var p in settings.GameProfiles)
        {
            if (!p.Enabled || p.ProcessName is null) continue;
            if (p.Route == GameRoute.Vpn) processes.Add(p.ProcessName);
        }
        // Auto profiles only after a measurement chose the VPS, and only while the profile is still an enabled
        // Auto profile for that exe (a late or stale decision must not keep traffic on the VPS).
        var autoExes = settings.GameProfiles
            .Where(p => p.Enabled && p.Route == GameRoute.Auto && p.ProcessName is not null)
            .Select(p => p.ProcessName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        processes.AddRange(autoVpnProcesses.Where(autoExes.Contains));
        return new VpnPlan(
            processes.Where(SingBoxConfig.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            settings.VpnDomains.ToList());
    }
}
