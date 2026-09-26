using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZapretHub.Core.Updates;

namespace ZapretHub.Core.Vpn;

/// <summary>Official sing-box releases (SagerNet/sing-box), Windows build for this CPU.</summary>
public sealed partial class SingBoxReleaseClient : GitHubReleaseSource
{
    public const long MaxDownloadBytes = 150L * 1024 * 1024;

    [GeneratedRegex(@"^sing-box-[0-9.]+-windows-amd64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex Amd64Asset();

    [GeneratedRegex(@"^sing-box-[0-9.]+-windows-arm64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex Arm64Asset();

    public SingBoxReleaseClient(HttpClient http, Architecture architecture, TimeSpan? downloadDeadline = null)
        : base(http, "SagerNet/sing-box", architecture == Architecture.Arm64 ? Arm64Asset() : Amd64Asset(), MaxDownloadBytes,
            downloadDeadline ?? TimeSpan.FromMinutes(10))
    {
    }
}

public sealed class SingBoxStore : VersionedFileStore
{
    public const string ExeName = "sing-box.exe";

    public SingBoxStore(string root) : base(root, ExeName)
    {
    }
}

/// <summary>Confirmed sing-box updates; only the known files are taken out of the release zip.</summary>
public sealed class SingBoxUpdater : FileReleaseUpdater
{
    private static readonly string[] Wanted = { SingBoxStore.ExeName, "libcronet.dll" };
    public const long MaxUnpackedBytes = 400L * 1024 * 1024;

    public SingBoxUpdater(SingBoxReleaseClient releases, SingBoxStore store) : base(releases, store, "sing-box")
    {
    }

    protected override IReadOnlyDictionary<string, byte[]> Unpack(Stream download) => UnpackZip(download);

    public static IReadOnlyDictionary<string, byte[]> UnpackZip(Stream download)
    {
        using var zip = new ZipArchive(download, ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.Name; // file name only: folder structure inside the zip is ignored
            if (!Wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (result.ContainsKey(name)) throw new InvalidDataException($"Archive contains {name} twice.");
            // Count what is actually decompressed: the sizes an archive declares can lie.
            using var s = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxUnpackedBytes) throw new InvalidDataException("Archive unpacks to more than expected.");
                buffer.Write(chunk, 0, read);
            }
            result[name] = buffer.ToArray();
        }
        if (!result.ContainsKey(SingBoxStore.ExeName)) throw new InvalidDataException("Archive has no sing-box.exe.");
        return result;
    }
}
