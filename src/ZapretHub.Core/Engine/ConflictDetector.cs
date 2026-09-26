using System.Diagnostics;

namespace ZapretHub.Core.Engine;

public sealed record ProcessInfo(string Name, int Id);

public sealed record ConflictReport(IReadOnlyList<ProcessInfo> Processes, IReadOnlyList<string> Services)
{
    public bool Any => Processes.Count > 0 || Services.Count > 0;
}

/// <summary>
/// Other DPI-bypass tools share the WinDivert driver; running two at once makes both misbehave.
/// </summary>
public static class ConflictDetector
{
    public static readonly IReadOnlyList<string> ProcessNames = new[] { "winws", "goodbyedpi" };

    /// <summary>Flowseal's service.bat installs "zapret"; GoodbyeDPI installs "GoodbyeDPI".
    /// The WinDivert driver service itself is shared and harmless once its owner stopped.</summary>
    public static readonly IReadOnlyList<string> ServiceNames = new[] { "zapret", "GoodbyeDPI" };

    public static ConflictReport Find(IEnumerable<ProcessInfo> running, IEnumerable<string> runningServices, int? ownProcessId) =>
        new(
            running
                .Where(p => ProcessNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Id != ownProcessId)
                .ToList(),
            runningServices
                .Where(s => ServiceNames.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList());

    public static IEnumerable<ProcessInfo> SnapshotCandidates()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p) yield return new ProcessInfo(name, p.Id);
            }
        }
    }
}
