using System.IO.Compression;
using ZapretHub.Core.Strategies;

namespace ZapretHub.Core.Updates;

public sealed record EngineLayout(
    string Version,
    string RootDir,
    string BinDir,
    string ListsDir,
    IReadOnlyList<StrategyDefinition> Strategies,
    IReadOnlyList<string> Problems)
{
    public string WinwsPath => Path.Combine(BinDir, "winws.exe");
}

/// <summary>
/// Keeps unpacked Flowseal releases in versions/&lt;ver&gt; and remembers which one is active.
/// A running winws locks its files, so a new release is unpacked side by side and
/// activated by switching the pointer rather than overwriting in place.
/// </summary>
public sealed class EngineStore
{
    private const string PointerFile = "active.txt";
    private const string IpsetPlaceholder = "203.0.113.113/32";
    public const int MaxEntries = 2000;
    public const long MaxUnpackedBytes = 200L * 1024 * 1024;
    private static readonly EnginePaths DryRunPaths = new(@"C:\bin", @"C:\lists", @"C:\user");

    private readonly string _root;
    private readonly string _versionsDir;

    public EngineStore(string root)
    {
        _root = root;
        _versionsDir = Path.Combine(root, "versions");
    }

    public string? ActiveVersion
    {
        get
        {
            var pointer = Path.Combine(_root, PointerFile);
            if (!File.Exists(pointer)) return null;
            string version;
            try
            {
                // Normalization rejects anything but digits and dots, so the pointer cannot leave versions/.
                version = ReleaseVersion.Normalize(File.ReadAllText(pointer));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            return Directory.Exists(Path.Combine(_versionsDir, version)) ? version : null;
        }
    }

    public EngineLayout? GetActive()
    {
        var version = ActiveVersion;
        return version is null ? null : Load(version, Path.Combine(_versionsDir, version));
    }

    public EngineLayout Install(Stream zip, string version)
    {
        version = ReleaseVersion.Normalize(version);
        Directory.CreateDirectory(_versionsDir);
        var staging = Path.Combine(_versionsDir, ".staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            Extract(zip, staging);
            var layout = Load(version, staging);
            Validate(layout);
            ActivateFullIpset(layout.ListsDir);

            var target = Path.Combine(_versionsDir, version);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);

            Activate(version);
            return Load(version, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Points the store at an already installed version (used to roll back a failed update).</summary>
    public void Activate(string version)
    {
        version = ReleaseVersion.Normalize(version);
        if (!Directory.Exists(Path.Combine(_versionsDir, version)))
        {
            throw new DirectoryNotFoundException($"Engine version {version} is not installed.");
        }
        var tmpPointer = Path.Combine(_root, PointerFile + ".tmp");
        File.WriteAllText(tmpPointer, version);
        File.Move(tmpPointer, Path.Combine(_root, PointerFile), overwrite: true);
    }

    /// <summary>Deletes non-active versions; ones still locked by a running winws are left for next time.</summary>
    public void CleanupInactive()
    {
        if (!Directory.Exists(_versionsDir)) return;
        var active = ActiveVersion;
        // Without a valid pointer we cannot tell which version is in use; deleting would risk the running one.
        if (active is null) return;
        foreach (var dir in Directory.GetDirectories(_versionsDir))
        {
            if (string.Equals(Path.GetFileName(dir), active, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by a running process; retry on next cleanup.
            }
        }
    }

    private static void Extract(Stream zipStream, string destination)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"Archive has {zip.Entries.Count} entries (limit {MaxEntries}).");
        }
        // Declared sizes guard against zip bombs; a Flowseal release unpacks to a few MB.
        if (zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
        {
            throw new InvalidDataException($"Archive unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB.");
        }
        var destRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var prefix = CommonTopFolder(zip);

        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (prefix is not null) relative = relative[prefix.Length..];
            if (relative.Length == 0) continue;

            var fullPath = Path.GetFullPath(Path.Combine(destRoot, relative));
            if (!fullPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes the target directory.");
            }

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            entry.ExtractToFile(fullPath, overwrite: true);
        }
    }

    // Release zips wrap everything in "zapret-discord-youtube-<ver>/"; strip it when every entry shares it.
    private static string? CommonTopFolder(ZipArchive zip)
    {
        string? prefix = null;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            if (slash <= 0) return null;
            var top = name[..(slash + 1)];
            if (prefix is null) prefix = top;
            else if (!string.Equals(prefix, top, StringComparison.Ordinal)) return null;
        }
        return prefix is "../" or "./" ? null : prefix;
    }

    private static EngineLayout Load(string version, string root)
    {
        var strategies = new List<StrategyDefinition>();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "general*.bat").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var strategy = BatStrategyParser.Parse(BatStrategyParser.NameFromFileName(file), File.ReadAllText(file));
                // Dry-run argument building so a release with an unknown placeholder is rejected at install
                // time, not after it has replaced a working version.
                foreach (var mode in Enum.GetValues<GameFilterMode>())
                {
                    StrategyArgsBuilder.Build(strategy, DryRunPaths, new GameFilterOptions(mode));
                }
                strategies.Add(strategy);
            }
            catch (FormatException ex)
            {
                problems.Add(ex.Message);
            }
        }
        return new EngineLayout(version, root, Path.Combine(root, "bin"), Path.Combine(root, "lists"), strategies, problems);
    }

    private static void Validate(EngineLayout layout)
    {
        if (!File.Exists(layout.WinwsPath))
        {
            throw new InvalidDataException("Release has no bin/winws.exe.");
        }
        if (!Directory.Exists(layout.ListsDir))
        {
            throw new InvalidDataException("Release has no lists directory.");
        }
        if (layout.Strategies.Count == 0)
        {
            throw new InvalidDataException("Release has no parsable general*.bat strategy: " + string.Join("; ", layout.Problems));
        }
    }

    // Flowseal ships ipset-all.txt as a one-line placeholder ("none" mode) with the real list in .backup.
    // We always run in "loaded" mode: "any" makes game rules hit every IP and breaks unrelated services.
    private static void ActivateFullIpset(string listsDir)
    {
        var active = Path.Combine(listsDir, "ipset-all.txt");
        var backup = active + ".backup";
        if (!File.Exists(backup)) return;
        var current = File.Exists(active) ? File.ReadAllText(active) : "";
        if (string.IsNullOrWhiteSpace(current) || current.Contains(IpsetPlaceholder, StringComparison.Ordinal))
        {
            File.Copy(backup, active, overwrite: true);
        }
    }
}
