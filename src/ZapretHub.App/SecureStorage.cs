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
        CreateOrLock(AppPaths.UserData, UserAcl());
        CreateOrLock(AppPaths.UserLists, UserAcl());
        Directory.CreateDirectory(AppPaths.GamesDir); // inherits the admin-only root ACL

        if (quarantined is not null) Log.Error(quarantined);
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
