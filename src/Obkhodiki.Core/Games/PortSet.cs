namespace Obkhodiki.Core.Games;

/// <summary>Immutable, normalized set of ports in winws syntax ("443,7000-7100").</summary>
public sealed class PortSet
{
    public static readonly PortSet Empty = new(Array.Empty<(int, int)>());

    private readonly (int From, int To)[] _ranges;

    private PortSet((int From, int To)[] ranges) => _ranges = ranges;

    public bool IsEmpty => _ranges.Length == 0;

    public static PortSet Parse(string text)
    {
        var ranges = new List<(int, int)>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('-');
            if (parts.Length > 2) throw new FormatException($"Invalid port range '{raw}'.");
            var from = ParsePort(parts[0]);
            var to = parts.Length == 2 ? ParsePort(parts[1]) : from;
            if (from > to) throw new FormatException($"Invalid port range '{raw}'.");
            ranges.Add((from, to));
        }
        return Normalize(ranges);
    }

    public static PortSet FromPorts(IEnumerable<int> ports) => Normalize(ports.Select(p => (ValidPort(p), p)));

    public PortSet Union(PortSet other) => Normalize(_ranges.Concat(other._ranges));

    public override string ToString() =>
        string.Join(",", _ranges.Select(r => r.From == r.To ? r.From.ToString() : $"{r.From}-{r.To}"));

    private static PortSet Normalize(IEnumerable<(int From, int To)> ranges)
    {
        var merged = new List<(int From, int To)>();
        foreach (var r in ranges.OrderBy(r => r.From))
        {
            if (merged.Count > 0 && r.From <= merged[^1].To + 1)
            {
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            }
            else
            {
                merged.Add(r);
            }
        }
        return new PortSet(merged.ToArray());
    }

    private static int ParsePort(string s) =>
        int.TryParse(s.Trim(), out var p) ? ValidPort(p) : throw new FormatException($"Invalid port '{s}'.");

    private static int ValidPort(int p) =>
        p is >= 1 and <= 65535 ? p : throw new FormatException($"Port {p} is outside 1-65535.");
}
