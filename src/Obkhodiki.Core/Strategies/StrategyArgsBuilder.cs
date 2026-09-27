using System.Text.RegularExpressions;
using Obkhodiki.Core.Games;

namespace Obkhodiki.Core.Strategies;

/// <param name="BinDir">Engine bin directory (winws.exe, fake payload .bin files).</param>
/// <param name="ListsDir">Engine lists shipped by Flowseal; replaced on every update.</param>
/// <param name="UserListsDir">User-owned *-user.txt lists; never touched by updates.</param>
public sealed record EnginePaths(string BinDir, string ListsDir, string UserListsDir);

public enum GameFilterMode { Disabled, Tcp, Udp, All }

public sealed record GameFilterOptions
{
    public const string DefaultRange = "1024-65535";

    public static readonly GameFilterOptions Disabled = new(GameFilterMode.Disabled);

    public GameFilterOptions(GameFilterMode mode, string tcpRange = DefaultRange, string udpRange = DefaultRange)
    {
        ValidateRange(tcpRange);
        ValidateRange(udpRange);
        Mode = mode;
        TcpRange = tcpRange;
        UdpRange = udpRange;
    }

    public GameFilterMode Mode { get; }
    public string TcpRange { get; }
    public string UdpRange { get; }

    private static void ValidateRange(string range)
    {
        var parts = range.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var from)
            || !int.TryParse(parts[1], out var to)
            || from < 1 || to > 65535 || from > to)
        {
            throw new ArgumentException($"Invalid port range '{range}'. Expected 'from-to' within 1-65535.");
        }
    }
}

public static partial class StrategyArgsBuilder
{
    // Flowseal points disabled game filters at port 12: a port nothing uses, so the rule is inert.
    private const string InertPort = "12";
    private const string UserListSuffix = "-user.txt";

    [GeneratedRegex("%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex Placeholder();

    private const string IpsetAllArg = "--ipset=%LISTS%ipset-all.txt";

    // Used when a strategy has no ipset-all blocks to copy the desync style from.
    private static readonly string[][] FallbackGameBlocks =
    {
        new[] { "--filter-tcp=%GameFilterTCP%", IpsetAllArg, "--dpi-desync=multisplit", "--dpi-desync-split-pos=1",
                "--dpi-desync-any-protocol=1", "--dpi-desync-cutoff=n3" },
        new[] { "--filter-udp=%GameFilterUDP%", IpsetAllArg, "--dpi-desync=fake", "--dpi-desync-repeats=6",
                "--dpi-desync-any-protocol=1", "--dpi-desync-fake-unknown-udp=%BIN%quic_initial_www_google_com.bin", "--dpi-desync-cutoff=n2" },
    };

    /// <param name="rules">Enabled game profiles. Each gets copies of the strategy's ipset-all blocks restricted to
    /// its own ipset and ports, i.e. Flowseal's "game filter + ipset any" behaviour, but only for that game.</param>
    public static IReadOnlyList<string> Build(
        StrategyDefinition strategy, EnginePaths paths, GameFilterOptions game, IReadOnlyList<GameRule>? rules = null)
    {
        var tcp = game.Mode is GameFilterMode.Tcp or GameFilterMode.All ? game.TcpRange : InertPort;
        var udp = game.Mode is GameFilterMode.Udp or GameFilterMode.All ? game.UdpRange : InertPort;
        var legacy = game.Mode switch
        {
            GameFilterMode.All or GameFilterMode.Tcp => game.TcpRange,
            GameFilterMode.Udp => game.UdpRange,
            _ => InertPort,
        };

        var args = strategy.Args.Select(arg => Resolve(arg, paths, tcp, udp, legacy)).ToList();
        if (rules is null || rules.Count == 0) return args;

        var templates = SplitBlocks(strategy.Args).Where(b => b.Any(IsIpsetAll)).ToList();
        if (templates.Count == 0) templates = FallbackGameBlocks.ToList();

        foreach (var rule in rules)
        {
            foreach (var block in templates)
            {
                var cloned = CloneForRule(block, rule);
                if (cloned is null) continue;
                args.Add("--new");
                args.AddRange(cloned.Select(arg => Resolve(arg, paths, tcp, udp, legacy)));
            }
        }

        ExtendCapture(args, "--wf-tcp=", rules.Aggregate(PortSet.Empty, (acc, r) => acc.Union(r.Tcp)));
        ExtendCapture(args, "--wf-udp=", rules.Aggregate(PortSet.Empty, (acc, r) => acc.Union(r.Udp)));
        return args;
    }

    private static bool IsIpsetAll(string arg) => arg.Equals(IpsetAllArg, StringComparison.OrdinalIgnoreCase);

    private static List<string[]> SplitBlocks(IReadOnlyList<string> args)
    {
        var blocks = new List<string[]>();
        var current = new List<string>();
        foreach (var a in args)
        {
            if (a == "--new")
            {
                blocks.Add(current.ToArray());
                current.Clear();
            }
            else
            {
                current.Add(a);
            }
        }
        blocks.Add(current.ToArray());
        return blocks;
    }

    /// <returns>Null when the block targets a protocol the rule has no ports for.</returns>
    private static string[]? CloneForRule(string[] block, GameRule rule)
    {
        var result = new List<string>();
        foreach (var arg in block)
        {
            // Capture filters are global options; they are extended once, not repeated per block.
            if (arg.StartsWith("--wf-", StringComparison.OrdinalIgnoreCase)) continue;

            if (IsIpsetAll(arg))
            {
                result.Add("--ipset=" + rule.IpsetPath);
                continue;
            }

            var value = arg;
            if (value.Contains("%GameFilterTCP%", StringComparison.OrdinalIgnoreCase))
            {
                if (rule.Tcp.IsEmpty) return null;
                value = Regex.Replace(value, "%GameFilterTCP%", rule.Tcp.ToString(), RegexOptions.IgnoreCase);
            }
            if (value.Contains("%GameFilterUDP%", StringComparison.OrdinalIgnoreCase))
            {
                if (rule.Udp.IsEmpty) return null;
                value = Regex.Replace(value, "%GameFilterUDP%", rule.Udp.ToString(), RegexOptions.IgnoreCase);
            }
            if (value.Contains("%GameFilter%", StringComparison.OrdinalIgnoreCase))
            {
                var ports = rule.Tcp.IsEmpty ? rule.Udp : rule.Tcp;
                value = Regex.Replace(value, "%GameFilter%", ports.ToString(), RegexOptions.IgnoreCase);
            }
            result.Add(value);
        }
        return result.ToArray();
    }

    private static void ExtendCapture(List<string> args, string prefix, PortSet ports)
    {
        if (ports.IsEmpty) return;
        var index = args.FindIndex(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) args[index] = args[index] + "," + ports;
        else args.Insert(0, prefix + ports);
    }

    private static string Resolve(string arg, EnginePaths paths, string tcp, string udp, string legacy)
    {
        var listsDir = WithSeparator(paths.ListsDir);
        var userListsDir = WithSeparator(paths.UserListsDir);

        return Placeholder().Replace(arg, m =>
        {
            var name = m.Groups[1].Value.ToUpperInvariant();
            switch (name)
            {
                case "BIN":
                    return WithSeparator(paths.BinDir);
                case "LISTS":
                    // Placeholder is followed by the file name: route *-user.txt lists to the user dir.
                    var rest = arg[(m.Index + m.Length)..];
                    var fileName = rest.Split(',', ';')[0];
                    return fileName.EndsWith(UserListSuffix, StringComparison.OrdinalIgnoreCase) ? userListsDir : listsDir;
                case "GAMEFILTERTCP":
                    return tcp;
                case "GAMEFILTERUDP":
                    return udp;
                case "GAMEFILTER":
                    return legacy;
                default:
                    throw new FormatException(
                        $"Unknown placeholder '%{m.Groups[1].Value}%' in strategy argument '{arg}'. Flowseal format may have changed.");
            }
        });
    }

    private static string WithSeparator(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
}
