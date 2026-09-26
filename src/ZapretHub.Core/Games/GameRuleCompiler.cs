namespace ZapretHub.Core.Games;

public sealed record CompiledGameRules(IReadOnlyList<GameRule> Rules, IReadOnlyList<string> Skipped);

/// <summary>
/// Turns enabled profiles into winws rules. winws gets a normalized copy of each address list in a runtime
/// folder, never the editable file itself: winws re-reads lists while running, and an emptied list means
/// "every address", which would silently make a game rule global.
/// </summary>
public static class GameRuleCompiler
{
    public static CompiledGameRules Compile(IEnumerable<GameProfile> profiles, string gamesDir, string runtimeDir)
    {
        var rules = new List<GameRule>();
        var skipped = new List<string>();
        Directory.CreateDirectory(runtimeDir);

        foreach (var p in profiles.Where(p => p.Enabled))
        {
            try
            {
                var fileName = GameProfiles.IpsetFileName(p.Id);
                var source = Path.Combine(gamesDir, fileName);
                var lines = File.Exists(source)
                    ? IpsetBuilder.Merge(File.ReadAllLines(source), Array.Empty<string>())
                    : Array.Empty<string>();
                var tcp = PortSet.Parse(p.TcpPorts);
                var udp = PortSet.Parse(p.UdpPorts);

                if (lines.Count == 0)
                {
                    skipped.Add($"{p.Id}: no valid addresses");
                    continue;
                }
                if (tcp.IsEmpty && udp.IsEmpty)
                {
                    skipped.Add($"{p.Id}: no ports");
                    continue;
                }

                var runtime = Path.Combine(runtimeDir, fileName);
                File.WriteAllLines(runtime, lines);
                rules.Add(new GameRule(runtime, tcp, udp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                // One broken profile must not stop bypass from starting for everything else.
                skipped.Add($"{p.Id}: {ex.Message}");
            }
        }
        return new CompiledGameRules(rules, skipped);
    }
}
