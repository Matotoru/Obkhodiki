using System.Security.AccessControl;
using System.Security.Principal;

namespace ZapretHub.App;

/// <summary>
/// Locks the data root so a non-admin process cannot plant binaries, strategies or pointers
/// that the elevated app (and its logon task) would later execute.
/// </summary>
internal static class SecureStorage
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Must run before anything (including the log) writes under the data root.</summary>
    public static void Prepare()
    {
        var quarantined = QuarantineIfUntrusted(AppPaths.Root);

        // Created together with its security descriptor: creating first and locking later would leave a
        // window where the inherited %ProgramData% ACL lets any user create (and own) subfolders.
        CreateOrLock(AppPaths.Root, RootAcl());
        // Secrets (VPS password, sing-box config) and logs (visited endpoints): not even readable by other users.
        CreateOrLock(AppPaths.VpnRoot, AdminOnlyAcl());
        CreateOrLock(Path.GetDirectoryName(AppPaths.Log)!, AdminOnlyAcl());
        var rebuilt = RebuildUserFoldersIfLoose();
        Directory.CreateDirectory(AppPaths.GamesDir); // inherits the admin-only root ACL

        if (quarantined is not null) Log.Error(quarantined);
        if (rebuilt is not null) Log.Info(rebuilt);
    }

    // Rights that would let a non-admin redirect or empty a user folder (mount point needs FILE_WRITE_DATA
    // on the folder; emptying it needs DELETE on files) or re-grant itself anything.
    private const FileSystemRights DangerousOnFolder =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly string[] UserDataFiles = { "targets.txt" };
    private const long MaxCopiedFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The user folders are never re-permissioned in place: setting an ACL by path on a folder the user may
    /// still control follows a junction swapped in at the last moment. If anything about them is loose
    /// (older app version, foreign owner, reparse point), they are moved aside, recreated atomically with the
    /// right ACL, and only plain known text files are copied back.
    /// </summary>
    private static string? RebuildUserFoldersIfLoose()
    {
        if (IsLockedDown(AppPaths.UserData, allowedSubdirs: new[] { "lists" }) && IsLockedDown(AppPaths.UserLists, allowedSubdirs: Array.Empty<string>()))
        {
            return null;
        }

        string? aside = null;
        var existing = new DirectoryInfo(AppPaths.UserData);
        if (existing.Exists || File.Exists(AppPaths.UserData))
        {
            aside = QuarantineName(AppPaths.UserData);
            if (existing.Exists && existing.Attributes.HasFlag(FileAttributes.ReparsePoint)) existing.Delete(); // removes the link only
            else if (existing.Exists) existing.MoveTo(aside); // renaming a junction moves the link, never its target
            else File.Move(AppPaths.UserData, aside);
        }

        new DirectoryInfo(AppPaths.UserData).Create(UserAcl());
        new DirectoryInfo(AppPaths.UserLists).Create(UserAcl());

        if (aside is not null && Directory.Exists(aside))
        {
            CopyPlainTextFiles(aside, AppPaths.UserData, name => UserDataFiles.Contains(name, StringComparer.OrdinalIgnoreCase));
            var oldLists = Path.Combine(aside, "lists");
            if (Directory.Exists(oldLists) && !new DirectoryInfo(oldLists).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                CopyPlainTextFiles(oldLists, AppPaths.UserLists, name => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            }
        }
        return $"User folder {AppPaths.UserData} re-created with locked-down permissions" + (aside is null ? "" : $"; previous copy kept at {aside}");
    }

    private static bool IsLockedDown(string path, string[] allowedSubdirs)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            var sec = dir.GetAccessControl();
            if (!IsTrustedOwner(sec.GetOwner(typeof(SecurityIdentifier))) || !sec.AreAccessRulesProtected) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference == Admins || rule.IdentityReference == System) continue;
                // Rules that apply to the folder itself must not carry anything dangerous.
                var appliesToFolder = !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly);
                if (appliesToFolder && (rule.FileSystemRights & DangerousOnFolder) != 0) return false;
                if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)) return false;
                if ((rule.FileSystemRights & FileSystemRights.Delete) != 0) return false;
            }

            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (entry is DirectoryInfo && !allowedSubdirs.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) return false;
                var owner = entry is FileInfo f
                    ? f.GetAccessControl().GetOwner(typeof(SecurityIdentifier))
                    : ((DirectoryInfo)entry).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                if (!IsTrustedOwner(owner)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            return false;
        }
    }

    // Copies only small regular files by content into new admin-owned files (so they inherit the new ACL).
    private static void CopyPlainTextFiles(string from, string to, Func<string, bool> wanted)
    {
        foreach (var file in new DirectoryInfo(from).EnumerateFiles())
        {
            if (!wanted(file.Name) || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaxCopiedFileBytes) continue;
            try
            {
                File.WriteAllBytes(Path.Combine(to, file.Name), File.ReadAllBytes(file.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable leftovers; defaults are recreated by the app.
            }
        }
    }

    // %ProgramData% lets any user create folders. A pre-created ZapretHub folder, a junction, a file in its
    // place, or anything inside the trusted part not owned by admins means we cannot trust it: move it aside.
    private static string? QuarantineIfUntrusted(string root)
    {
        if (File.Exists(root))
        {
            var asideFile = QuarantineName(root);
            File.Move(root, asideFile);
            return $"A file occupied the data folder path {root}; moved to {asideFile}";
        }
        if (!Directory.Exists(root)) return null;

        string? problem;
        try
        {
            problem = FindUntrusted(new DirectoryInfo(root));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            // Unreadable security info (e.g. an empty DACL set by whoever created it) is itself untrusted.
            problem = "unreadable security descriptor: " + ex.Message;
        }
        if (problem is null) return null;

        var info = new DirectoryInfo(root);
        var aside = QuarantineName(root);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
        else info.MoveTo(aside);
        return $"Data folder {root} is not trustworthy ({problem}); moved aside to {aside}";
    }

    private static string QuarantineName(string path) =>
        $"{path}.untrusted-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

    // Walks the admin-only part of the tree. The user folder is excluded: users are allowed to write there.
    private static string? FindUntrusted(DirectoryInfo dir)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"reparse point {dir.FullName}";
        if (!IsTrustedOwner(dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) return $"foreign owner of {dir.FullName}";
        if (string.Equals(dir.FullName.TrimEnd('\\'), AppPaths.UserData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var file in dir.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"reparse point {file.FullName}";
            if (!IsTrustedOwner(file.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) return $"foreign owner of {file.FullName}";
        }
        foreach (var sub in dir.EnumerateDirectories())
        {
            var problem = FindUntrusted(sub);
            if (problem is not null) return problem;
        }
        return null;
    }

    private static bool IsTrustedOwner(IdentityReference? owner) => owner == Admins || owner == System;

    private static void CreateOrLock(string path, DirectorySecurity acl)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"{path} is a junction/symlink; refusing to use it.");
            }
            info.SetAccessControl(acl);
        }
        else
        {
            info.Create(acl);
        }
    }

    private static DirectorySecurity AdminOnlyAcl()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(Inherited(Admins, FileSystemRights.FullControl));
        sec.AddAccessRule(Inherited(System, FileSystemRights.FullControl));
        return sec;
    }

    private static DirectorySecurity RootAcl()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(Inherited(Admins, FileSystemRights.FullControl));
        sec.AddAccessRule(Inherited(System, FileSystemRights.FullControl));
        sec.AddAccessRule(Inherited(Users, FileSystemRights.ReadAndExecute));
        return sec;
    }

    // The installing user may edit the files the app created, and nothing else:
    // - no write access to the folder itself: FILE_WRITE_DATA (= "create files") on a folder is enough to
    //   turn it into a mount point, which would redirect the elevated app's writes anywhere;
    // - no DELETE on files: the folder can never be emptied, which a mount point requires anyway.
    // The app pre-creates every file the user is meant to edit.
    private static DirectorySecurity UserAcl()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(Inherited(Admins, FileSystemRights.FullControl));
        sec.AddAccessRule(Inherited(System, FileSystemRights.FullControl));

        var user = WindowsIdentity.GetCurrent().User ?? Users;
        sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute | FileSystemRights.Write,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        return sec;
    }

    private static FileSystemAccessRule Inherited(SecurityIdentifier sid, FileSystemRights rights) =>
        new(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
}
