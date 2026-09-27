namespace Obkhodiki.Core.Testing;

/// <summary>
/// Parses Flowseal's utils/targets.txt format: <c>Name = "https://host"</c>.
/// PING-only entries are skipped: ICMP says nothing about DPI blocking.
/// </summary>
public static class TargetsFile
{
    public static IReadOnlyList<ProbeTarget> Parse(string text)
    {
        var targets = new List<ProbeTarget>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;

            targets.Add(new ProbeTarget(name, uri));
        }
        return targets;
    }
}
