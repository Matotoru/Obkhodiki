using System.Text;

namespace ZapretHub.Core.Strategies;

public sealed record StrategyDefinition(string Name, IReadOnlyList<string> Args);

/// <summary>
/// Extracts the winws.exe argument list from a Flowseal general*.bat file.
/// Only the winws command is used; the surrounding batch logic (service calls, cd) is ignored.
/// </summary>
public static class BatStrategyParser
{
    private const string WinwsMarker = "winws.exe\"";

    public static string NameFromFileName(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    public static StrategyDefinition Parse(string name, string batText)
    {
        var commands = JoinContinuations(batText)
            .Where(l => l.Contains(WinwsMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (commands.Count == 0)
        {
            throw new FormatException($"Strategy '{name}': no winws.exe command found.");
        }
        if (commands.Count > 1)
        {
            // Guessing which launch is "the" strategy could run the wrong arguments.
            throw new FormatException($"Strategy '{name}': {commands.Count} winws.exe commands found, expected one.");
        }
        var command = commands[0];

        var start = command.IndexOf(WinwsMarker, StringComparison.OrdinalIgnoreCase) + WinwsMarker.Length;
        var args = Tokenize(command[start..], name);
        if (args.Count == 0)
        {
            throw new FormatException($"Strategy '{name}': winws.exe has no arguments.");
        }
        return new StrategyDefinition(name, args);
    }

    // cmd.exe treats a trailing '^' as "continue on next line".
    private static IEnumerable<string> JoinContinuations(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.EndsWith('^'))
            {
                current.Append(line, 0, line.Length - 1).Append(' ');
                continue;
            }
            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static List<string> Tokenize(string text, string name)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (escaped)
            {
                token.Append(c);
                hasToken = true;
                escaped = false;
            }
            else if (c == '^' && !inQuotes)
            {
                // cmd escape: "^!" means a literal "!" (e.g. --dpi-desync-fake-tls=^! in FAKE TLS AUTO).
                escaped = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(token.ToString());
                token.Clear();
                hasToken = false;
            }
            else
            {
                token.Append(c);
                hasToken = true;
            }
        }

        if (inQuotes)
        {
            throw new FormatException($"Strategy '{name}': unbalanced quotes in winws arguments.");
        }
        if (hasToken) result.Add(token.ToString());
        return result;
    }
}
