using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.Core.Vpn;

/// <summary>
/// amnezia-box: Amnezia's sing-box fork with the AmneziaWG endpoint. Amnezia publishes source only, so this
/// repository's "amnezia-box" workflow builds the pinned tag and attaches the zip to a pre-release here. The app
/// installs exactly that build (version and SHA-256 are fixed in the code) and moves to a new one with an app update.
/// </summary>
public static class AmneziaBox
{
    public const string Version = "1.260910.0";
    public const string SourceCommit = "16724484cfa00d9b2c14d8280dc055564e2e12ba";
    public const string ExeName = "amnezia-box.exe";
    public const string ReleaseTag = "amnezia-box-v" + Version;

    // Filled from the workflow's output after it built the release (see .github/workflows/amnezia-box.yml).
    private const string Amd64Sha256 = "b91797a2b3871b3ce72d57d42b9f8db0c5509f650a947f9f3f4af38440c0f4c2";
    private const string Arm64Sha256 = "bc8cbcb84b50a29b3e4e5f151ea4a27ba7a836e379a6e1da23f850a25cc8a8e3";

    public static string AssetName(Architecture architecture) =>
        $"amnezia-box-{Version}-windows-{(architecture == Architecture.Arm64 ? "arm64" : "amd64")}.zip";

    public static ReleaseInfo Release(Architecture architecture) => new(
        Version,
        new Uri($"https://github.com/Matotoru/Obkhodiki/releases/download/{ReleaseTag}/{AssetName(architecture)}"),
        architecture == Architecture.Arm64 ? Arm64Sha256 : Amd64Sha256);
}

/// <summary>Always "finds" the pinned build; the download is checked against the pinned SHA-256.</summary>
public sealed partial class AmneziaBoxReleaseClient : GitHubReleaseSource
{
    public const long MaxDownloadBytes = 100L * 1024 * 1024;
    private readonly Architecture _architecture;

    [GeneratedRegex(@"^amnezia-box-[0-9.]+-windows-(amd64|arm64)\.zip$")]
    private static partial Regex Asset();

    public AmneziaBoxReleaseClient(HttpClient http, Architecture architecture, TimeSpan? downloadDeadline = null)
        : base(http, "Matotoru/Obkhodiki", Asset(), MaxDownloadBytes, downloadDeadline ?? TimeSpan.FromMinutes(10))
    {
        _architecture = architecture;
    }

    public override Task<ReleaseInfo> GetLatestAsync(CancellationToken ct) => Task.FromResult(AmneziaBox.Release(_architecture));
}

public sealed class AmneziaBoxUpdater : FileReleaseUpdater
{
    public AmneziaBoxUpdater(AmneziaBoxReleaseClient releases, VersionedFileStore store) : base(releases, store, "amnezia-box")
    {
    }

    public static VersionedFileStore CreateStore(string root) => new(root, AmneziaBox.ExeName);

    protected override IReadOnlyDictionary<string, byte[]> Unpack(Stream download)
    {
        using var zip = new ZipArchive(download, ZipArchiveMode.Read);
        var entry = zip.Entries.SingleOrDefault(e => e.Name.Equals(AmneziaBox.ExeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("Archive has no amnezia-box.exe.");
        using var s = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > SingBoxUpdater.MaxUnpackedBytes) throw new InvalidDataException("Archive unpacks to more than expected.");
            buffer.Write(chunk, 0, read);
        }
        return new Dictionary<string, byte[]> { [AmneziaBox.ExeName] = buffer.ToArray() };
    }
}
