using System.Security.Principal;

namespace Obkhodiki.App;

/// <summary>
/// Carries the data of the app from before its rename (ZapretHub) into the new data folder: engines, settings,
/// games, the Telegram proxy and the encrypted VPS source. Copied, not moved: files the old app, its proxy or
/// the WinDivert driver still hold open cannot stop it, and the old folder stays as a fallback.
/// </summary>
internal static class LegacyMigration
{
    private static readonly string LegacyRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretHub");

    // Not carried over: logs and leftovers of an update; "user" is copied separately (user-writable there).
    private static readonly string[] SkippedFolders = { "logs", "update", "user" };
    private const long MaxUserFileBytes = 4 * 1024 * 1024;

    /// <returns>A log line when something was copied (the log is not available before Prepare).</returns>
    public static string? CopyDataIfNeeded()
    {
        if (File.Exists(AppPaths.Settings) || !Directory.Exists(LegacyRoot) || !IsTrusted(LegacyRoot)) return null;

        var copied = 0;
        var failed = new List<string>();
        foreach (var entry in new DirectoryInfo(LegacyRoot).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (entry is DirectoryInfo dir)
            {
                if (SkippedFolders.Contains(dir.Name, StringComparer.OrdinalIgnoreCase)) continue;
                copied += CopyTree(dir, Path.Combine(AppPaths.Root, dir.Name), failed);
            }
            else if (entry is FileInfo file)
            {
                copied += CopyFile(file, Path.Combine(AppPaths.Root, file.Name), failed);
            }
        }

        // The user folder was writable for normal users: only plain text lists and targets come across.
        var legacyUser = Path.Combine(LegacyRoot, "user");
        CopyPlainText(legacyUser, AppPaths.UserData, "targets.txt", failed, ref copied);
        CopyPlainText(Path.Combine(legacyUser, "lists"), AppPaths.UserLists, "*.txt", failed, ref copied);

        return $"Copied {copied} files from the old ZapretHub data folder {LegacyRoot}" +
               (failed.Count > 0 ? $"; not copied: {string.Join(", ", failed.Take(10))}" : "");
    }

    // The old app created and locked its folder; anyone may create folders in %ProgramData%, so a folder
    // owned by someone else is not trusted as a source of engines or settings.
    private static bool IsTrusted(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }

    private static int CopyTree(DirectoryInfo source, string target, List<string> failed)
    {
        var count = 0;
        Directory.CreateDirectory(target);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var dest = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo dir) count += CopyTree(dir, dest, failed);
            else if (entry is FileInfo file) count += CopyFile(file, dest, failed);
        }
        return count;
    }

    private static int CopyFile(FileInfo file, string dest, List<string> failed)
    {
        if (File.Exists(dest)) return 0;
        try
        {
            file.CopyTo(dest);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(file.Name);
            return 0;
        }
    }

    private static void CopyPlainText(string from, string to, string pattern, List<string> failed, ref int copied)
    {
        if (!Directory.Exists(from) || new DirectoryInfo(from).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (var file in new DirectoryInfo(from).EnumerateFiles(pattern))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaxUserFileBytes) continue;
            var dest = Path.Combine(to, file.Name);
            try
            {
                // Content only (a new file in the locked-down folder), never the old file's permissions.
                File.WriteAllBytes(dest, File.ReadAllBytes(file.FullName));
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(file.Name);
            }
        }
    }
}
