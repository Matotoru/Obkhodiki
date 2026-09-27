namespace Obkhodiki.Core.Updates;

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null, string? version = null, bool releaseDefect = false)
        : base(message, inner)
    {
        Version = version;
        ReleaseDefect = releaseDefect;
    }

    /// <summary>Release version the failure relates to, when known.</summary>
    public string? Version { get; }

    /// <summary>True when the release itself is bad (checksum mismatch, unusable archive, fails to start),
    /// as opposed to a transient network problem. Only defective releases should be skipped from then on.</summary>
    public bool ReleaseDefect { get; }
}

/// <summary>Numeric dotted versions as used by Flowseal tags ("1.10.3"; version.txt may carry a trailing dot).</summary>
public static class ReleaseVersion
{
    public static string Normalize(string version)
    {
        var v = version.Trim().TrimStart('v', 'V').TrimEnd('.');
        var parts = v.Split('.');
        // Up to 9 digits per part: always fits an int, so comparing can never overflow.
        if (v.Length == 0 || parts.Any(p => p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit)))
        {
            throw new FormatException($"Unsupported version '{version}'.");
        }
        return v;
    }

    public static bool IsNewer(string candidate, string? current)
    {
        if (current is null) return true;
        return Compare(Normalize(candidate), Normalize(current)) > 0;
    }

    private static int Compare(string a, string b)
    {
        var x = a.Split('.').Select(int.Parse).ToArray();
        var y = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
