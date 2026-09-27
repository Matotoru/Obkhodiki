using Obkhodiki.Core.Vpn;

namespace Obkhodiki.App;

/// <summary>
/// Downloaded rule-set files for the routing categories. They only steer routing (never executed), are checked
/// for the binary rule-set header and a size cap, and live in the admin-only VPS folder.
/// </summary>
internal static class RuleSetStore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static string PathFor(RuleSetFile file) => Path.Combine(AppPaths.RuleSetsDir, file.Tag + ".srs");

    public static bool Has(RuleSetFile file) => File.Exists(PathFor(file));

    public static bool IsStale(RuleSetFile file) =>
        !Has(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(PathFor(file)) > MaxAge;

    private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> Stamps = new();

    /// <summary>Content stamp for the config signature: a changed file must restart sing-box, a refreshed date must not.</summary>
    public static string Stamp(RuleSetFile file)
    {
        var info = new FileInfo(PathFor(file));
        if (!info.Exists) return "-";
        lock (Stamps)
        {
            if (Stamps.TryGetValue(file.Tag, out var s) && s.Length == info.Length && s.Written == info.LastWriteTimeUtc) return s.Hash;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(info.FullName)))[..16];
            Stamps[file.Tag] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
    }

    /// <param name="validate">Checks the downloaded file (by path) can be loaded; throws when it cannot.</param>
    /// <returns>True when the file content changed.</returns>
    public static async Task<bool> DownloadAsync(RuleSetFile file, HttpClient http, Func<string, Task> validate, CancellationToken ct)
    {
        using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > RuleSetFile.MaxSize) throw new InvalidDataException($"{file.Tag}: файл слишком большой.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > RuleSetFile.MaxSize) throw new InvalidDataException($"{file.Tag}: файл слишком большой.");
        }
        var data = buffer.ToArray();
        if (!RuleSetFile.LooksValid(data)) throw new InvalidDataException($"{file.Tag}: это не файл правил sing-box.");

        Directory.CreateDirectory(AppPaths.RuleSetsDir);
        var path = PathFor(file);
        if (File.Exists(path) && (await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(data))
        {
            // Same content: only mark it fresh, no restart needed.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return false;
        }
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        try
        {
            // A file the installed sing-box cannot read would stop the whole tunnel at startup.
            await validate(tmp).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        File.Move(tmp, path, overwrite: true);
        return true;
    }
}
