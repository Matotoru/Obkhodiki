using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Updates;

/// <summary>
/// File-level steps of the app's self-update, kept free of process handling so they can be tested:
/// unpacking a (digest-verified) release zip and swapping the files of an install with a backup to roll back to.
/// </summary>
public static partial class UpdatePackage
{
    public const int MaxFiles = 64;

    // Root-level program files only: no folders, no "..", no ':' (alternate streams), known extensions.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.(exe|dll|json)\z", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    public static bool IsAllowedName(string name) => FileName().IsMatch(name) && !name.Contains("..");

    /// <summary>
    /// Unpacks every root-level program file (the set may grow between versions, so it is not a fixed list)
    /// into an empty <paramref name="dir"/>, and checks the files the app cannot start without are there.
    /// </summary>
    public static void Extract(Stream zip, string dir, IEnumerable<string> required, long maxFileBytes)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // folder entry
                if (!IsAllowedName(entry.FullName)) continue;
                if (!seen.Add(entry.FullName)) throw new InvalidDataException($"В архиве дважды есть {entry.FullName}.");
                if (seen.Count > MaxFiles) throw new InvalidDataException("В архиве слишком много файлов.");
                var dest = Path.Combine(dir, entry.FullName);
                using (var input = entry.Open())
                using (var output = File.Create(dest))
                {
                    // Counted while copying: the declared size in the zip is not trusted.
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        total += read;
                        if (total > maxFileBytes) throw new InvalidDataException($"{entry.FullName} в архиве слишком большой.");
                        output.Write(buffer, 0, read);
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("Архив обновления повреждён: " + ex.Message, ex);
        }

        var missing = required.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
        if (missing.Count > 0) throw new InvalidDataException("В архиве обновления нет файлов: " + string.Join(", ", missing));
    }

    /// <summary>Deletes every top-level file of <paramref name="dir"/> whose name is not in <paramref name="keep"/>.</summary>
    public static void KeepOnly(string dir, IEnumerable<string> keep)
    {
        var names = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(dir))
        {
            if (!names.Contains(Path.GetFileName(path))) File.Delete(path);
        }
    }

    /// <summary>What <see cref="Swap"/> did, to undo it.</summary>
    public sealed record SwapResult(IReadOnlyList<string> Replaced, IReadOnlyList<string> Added);

    /// <summary>
    /// Copies every program file of <paramref name="source"/> into <paramref name="target"/>, saving the files it
    /// overwrites into <paramref name="backup"/>. On failure everything done so far is undone before rethrowing.
    /// Refuses links: an elevated copy must not follow a junction or symlink somewhere else.
    /// </summary>
    public static SwapResult Swap(string source, string target, string backup, Action<string, string>? copy = null)
    {
        copy ??= (from, to) => File.Copy(from, to, overwrite: true);
        RefuseLink(target);
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        Directory.CreateDirectory(backup);

        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var path in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(path);
                if (!IsAllowedName(name)) continue;
                var dest = Path.Combine(target, name);
                if (File.Exists(dest))
                {
                    RefuseLink(dest);
                    File.Copy(dest, Path.Combine(backup, name), overwrite: true);
                    replaced.Add(name);
                }
                else
                {
                    added.Add(name);
                }
                copy(path, dest);
            }
            return new SwapResult(replaced, added);
        }
        catch
        {
            Restore(target, backup, new SwapResult(replaced, added));
            throw;
        }
    }

    /// <summary>Puts the backed-up files back and removes the ones the update added.</summary>
    /// <returns>False when something could not be restored (keep the backup for a later retry).</returns>
    public static bool Restore(string target, string backup, SwapResult swap, Action<string, string>? copy = null)
    {
        copy ??= (from, to) => File.Copy(from, to, overwrite: true);
        var ok = true;
        foreach (var name in swap.Replaced)
        {
            try
            {
                copy(Path.Combine(backup, name), Path.Combine(target, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        foreach (var name in swap.Added)
        {
            try
            {
                var path = Path.Combine(target, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    private static void RefuseLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} — ссылка, обновление туда не пишется.");
    }
}
