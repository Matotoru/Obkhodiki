using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Strategies;

/// <summary>
/// User-owned lists referenced by Flowseal strategies. winws refuses empty or missing
/// list files, so each gets a harmless placeholder entry (same defaults as Flowseal's service.bat).
/// </summary>
public static partial class UserLists
{
    private const string DomainPlaceholder = "domain.example.abc\n";
    private const string IpPlaceholder = "203.0.113.113/32\n";

    private static readonly (string File, string Content)[] Defaults =
    {
        ("list-general-user.txt", "# Never leave this file empty\n" + DomainPlaceholder),
        ("list-exclude-user.txt", DomainPlaceholder),
        ("ipset-exclude-user.txt", IpPlaceholder),
    };

    [GeneratedRegex(@"%LISTS%([^,;%]+-user\.txt)", RegexOptions.IgnoreCase)]
    private static partial Regex UserListReference();

    public static void EnsureDefaults(string userListsDir)
    {
        Directory.CreateDirectory(userListsDir);
        foreach (var (file, content) in Defaults)
        {
            CreateIfMissing(Path.Combine(userListsDir, file), content);
        }
    }

    /// <summary>Creates any *-user.txt a (possibly newer) Flowseal strategy references but we don't know yet.</summary>
    public static void EnsureReferenced(IEnumerable<StrategyDefinition> strategies, string userListsDir)
    {
        Directory.CreateDirectory(userListsDir);
        foreach (var name in ReferencedFiles(strategies))
        {
            var content = name.StartsWith("ipset", StringComparison.OrdinalIgnoreCase) ? IpPlaceholder : DomainPlaceholder;
            CreateIfMissing(Path.Combine(userListsDir, name), content);
        }
    }

    public static IReadOnlySet<string> ReferencedFiles(IEnumerable<StrategyDefinition> strategies) =>
        strategies
            .SelectMany(s => s.Args)
            .SelectMany(a => UserListReference().Matches(a).Select(m => m.Groups[1].Value))
            // A reference like "%LISTS%..\x-user.txt" must not escape the user folder.
            .Where(n => Path.GetFileName(n) == n)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void CreateIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Created concurrently (e.g. by the user) — keep theirs.
        }
    }
}
