namespace Obkhodiki.Core.Updates;

/// <summary>
/// Keeps downloaded builds of a helper program in versions/&lt;ver&gt;/ and remembers the active one.
/// A new version is written beside the running one and activated by switching a pointer.
/// </summary>
public class VersionedFileStore
{
    private const string PointerFile = "active.txt";

    private readonly string _root;
    private readonly string _versionsDir;

    /// <param name="mainFile">The executable that must be present in every version.</param>
    public VersionedFileStore(string root, string mainFile)
    {
        _root = root;
        _versionsDir = Path.Combine(root, "versions");
        MainFile = mainFile;
    }

    public string MainFile { get; }

    public string? ActiveVersion
    {
        get
        {
            var pointer = Path.Combine(_root, PointerFile);
            try
            {
                if (!File.Exists(pointer)) return null;
                var version = ReleaseVersion.Normalize(File.ReadAllText(pointer));
                return File.Exists(MainPath(version)) ? version : null;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public string? ActiveMain => ActiveVersion is { } v ? MainPath(v) : null;

    public string MainPath(string version) => Path.Combine(_versionsDir, ReleaseVersion.Normalize(version), MainFile);

    /// <summary>Stores a verified build (file name → content) and makes it active.</summary>
    public string Install(IReadOnlyDictionary<string, byte[]> files, string version)
    {
        version = ReleaseVersion.Normalize(version);
        if (!files.TryGetValue(MainFile, out var main) || !LooksLikeWindowsExecutable(main))
        {
            throw new InvalidDataException($"The download has no usable {MainFile}.");
        }
        foreach (var name in files.Keys)
        {
            // Only plain file names: nothing from an archive may choose where it lands.
            if (Path.GetFileName(name) != name || name.Length == 0) throw new InvalidDataException($"Unexpected file name '{name}'.");
        }

        Directory.CreateDirectory(_versionsDir);
        var staging = Path.Combine(_versionsDir, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, content) in files) File.WriteAllBytes(Path.Combine(staging, name), content);

            var target = Path.Combine(_versionsDir, version);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            Activate(version);
            return MainPath(version);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Removes a version; if it was active, nothing is active afterwards.</summary>
    public void Uninstall(string version)
    {
        version = ReleaseVersion.Normalize(version);
        if (ActiveVersion == version) File.Delete(Path.Combine(_root, PointerFile));
        var dir = Path.Combine(_versionsDir, version);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public void Activate(string version)
    {
        version = ReleaseVersion.Normalize(version);
        if (!File.Exists(MainPath(version))) throw new FileNotFoundException($"Version {version} is not installed.");
        var tmp = Path.Combine(_root, PointerFile + ".tmp");
        File.WriteAllText(tmp, version);
        File.Move(tmp, Path.Combine(_root, PointerFile), overwrite: true);
    }

    /// <summary>Removes non-active versions. Ones still locked by a running program are left and retried next time.</summary>
    /// <returns>Folders that could not be removed yet.</returns>
    public IReadOnlyList<string> CleanupInactive()
    {
        var left = new List<string>();
        var active = ActiveVersion;
        if (active is null || !Directory.Exists(_versionsDir)) return left;
        foreach (var dir in Directory.GetDirectories(_versionsDir))
        {
            if (string.Equals(Path.GetFileName(dir), active, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(dir);
            }
        }
        return left;
    }

    private static bool LooksLikeWindowsExecutable(byte[] content) =>
        content.Length >= 64 && content[0] == (byte)'M' && content[1] == (byte)'Z';
}

/// <summary>
/// Check → (user confirms) → download → verify → store → test-start → cleanup, with rollback to the previous
/// version when the new one does not start. Shared by the helper programs the app manages.
/// </summary>
public abstract class FileReleaseUpdater
{
    private readonly GitHubReleaseSource _releases;
    private readonly VersionedFileStore _store;
    private readonly string _displayName;

    protected FileReleaseUpdater(GitHubReleaseSource releases, VersionedFileStore store, string displayName)
    {
        _releases = releases;
        _store = store;
        _displayName = displayName;
    }

    /// <summary>Turns the verified download into the files to store (e.g. picks files out of a zip).</summary>
    protected abstract IReadOnlyDictionary<string, byte[]> Unpack(Stream download);

    public async Task<ReleaseInfo?> CheckAsync(string? skipVersion, CancellationToken ct)
    {
        var latest = await _releases.GetLatestAsync(ct).ConfigureAwait(false);
        if (!ReleaseVersion.IsNewer(latest.Version, _store.ActiveVersion)) return null;
        if (skipVersion is not null && latest.Version == ReleaseVersion.Normalize(skipVersion)) return null;
        return latest;
    }

    /// <param name="switchTo">Starts the new version; if it throws, the previous version is re-activated,
    /// passed to <paramref name="rollback"/>, and the failure is reported as a defective release.</param>
    /// <returns>The installed version, or null when <paramref name="release"/> is not newer than the active one.</returns>
    public async Task<string?> InstallAsync(ReleaseInfo release, Func<string, Task>? switchTo, Func<string, Task>? rollback, CancellationToken ct)
    {
        if (!ReleaseVersion.IsNewer(release.Version, _store.ActiveVersion)) return null;

        var previous = _store.ActiveVersion;
        string main;
        try
        {
            await using var download = await _releases.DownloadAsync(release, ct).ConfigureAwait(false);
            main = _store.Install(Unpack(download), release.Version);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException($"{_displayName} {release.Version} is not usable: {ex.Message}", ex, release.Version, releaseDefect: true);
        }
        catch (UpdateException ex) when (ex.Version is null)
        {
            throw new UpdateException(ex.Message, ex.InnerException, release.Version, ex.ReleaseDefect);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Local disk trouble, not a bad release: report it through the normal update error path.
            throw new UpdateException($"{_displayName} {release.Version} could not be stored: {ex.Message}", ex, release.Version);
        }

        if (switchTo is not null)
        {
            try
            {
                await switchTo(main).ConfigureAwait(false);
            }
            catch (Exception ex) when (previous is null)
            {
                // First install: nothing to go back to, so do not leave a build that cannot even start.
                try
                {
                    _store.Uninstall(release.Version);
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                }
                throw new UpdateException($"{_displayName} {release.Version} failed to start: {ex.Message}", ex, release.Version, releaseDefect: true);
            }
            catch (Exception ex) when (previous is not null)
            {
                _store.Activate(previous);
                var note = "";
                if (rollback is not null)
                {
                    try
                    {
                        await rollback(_store.MainPath(previous)).ConfigureAwait(false);
                    }
                    catch (Exception rollbackEx)
                    {
                        // Still a defective release; the caller learns the old version did not come back either.
                        note = $" Restarting {previous} also failed: {rollbackEx.Message}";
                    }
                }
                throw new UpdateException($"{_displayName} {release.Version} failed to start; rolled back to {previous}: {ex.Message}{note}",
                    ex, release.Version, releaseDefect: true);
            }
        }

        _store.CleanupInactive();
        return release.Version;
    }
}
