using System.IO.Compression;
using System.Text;
using ZapretHub.Core.Updates;

namespace ZapretHub.Core.Tests.Updates;

public sealed class EngineStoreTests : IDisposable
{
    private const string ValidBat = "start \"z\" /min \"%BIN%winws.exe\" --wf-tcp=443 --filter-tcp=443 --dpi-desync=fake";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zh-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    internal static MemoryStream Zip(Dictionary<string, string> files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(path);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // deterministic bytes for digests
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    internal static Dictionary<string, string> ValidRelease(string prefix = "zapret-discord-youtube-1.10.3/") => new()
    {
        [prefix + "bin/winws.exe"] = "exe",
        [prefix + "bin/WinDivert.dll"] = "dll",
        [prefix + "general.bat"] = ValidBat,
        [prefix + "general (ALT).bat"] = ValidBat,
        [prefix + "service.bat"] = "@echo off",
        [prefix + "lists/list-general.txt"] = "discord.com",
        [prefix + "lists/ipset-all.txt"] = "203.0.113.113/32",
        [prefix + "lists/ipset-all.txt.backup"] = "1.2.3.0/24",
    };

    [Fact]
    public void Install_ValidRelease_StripsTopFolderAndBecomesActive()
    {
        var store = new EngineStore(_root);

        var layout = store.Install(Zip(ValidRelease()), "1.10.3");

        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.True(File.Exists(Path.Combine(layout.BinDir, "winws.exe")));
        Assert.True(File.Exists(Path.Combine(layout.ListsDir, "list-general.txt")));
        Assert.Equal(new[] { "general", "general (ALT)" }, layout.Strategies.Select(s => s.Name).Order());
    }

    [Fact]
    public void Install_ServiceBatIsNotTreatedAsStrategy()
    {
        var layout = new EngineStore(_root).Install(Zip(ValidRelease()), "1.10.3");

        Assert.DoesNotContain(layout.Strategies, s => s.Name == "service");
    }

    [Fact]
    public void Install_ActivatesFullIpsetInsteadOfFlowsealPlaceholder()
    {
        var layout = new EngineStore(_root).Install(Zip(ValidRelease()), "1.10.3");

        Assert.Equal("1.2.3.0/24", File.ReadAllText(Path.Combine(layout.ListsDir, "ipset-all.txt")).Trim());
    }

    [Fact]
    public void Install_ArchiveWithoutTopFolder_Works()
    {
        var layout = new EngineStore(_root).Install(Zip(ValidRelease(prefix: "")), "1.10.3");

        Assert.True(File.Exists(Path.Combine(layout.BinDir, "winws.exe")));
    }

    public static IEnumerable<object[]> BrokenReleases() => new[]
    {
        new object[] { "missing winws", (Action<Dictionary<string, string>>)(r => r.Remove("zapret-discord-youtube-1.10.3/bin/winws.exe")) },
        new object[] { "no lists", (Action<Dictionary<string, string>>)(r =>
        {
            foreach (var k in r.Keys.Where(k => k.Contains("/lists/")).ToList()) r.Remove(k);
        }) },
        new object[] { "unparsable strategies", (Action<Dictionary<string, string>>)(r =>
        {
            r["zapret-discord-youtube-1.10.3/general.bat"] = "echo changed";
            r["zapret-discord-youtube-1.10.3/general (ALT).bat"] = "echo changed";
        }) },
        new object[] { "zip slip", (Action<Dictionary<string, string>>)(r => r["zapret-discord-youtube-1.10.3/../../evil.txt"] = "x") },
    };

    [Theory]
    [MemberData(nameof(BrokenReleases))]
    public void Install_BrokenNewVersion_PreviousStaysActiveAndNothingLeftBehind(string _, Action<Dictionary<string, string>> breakIt)
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.3");
        var broken = ValidRelease();
        breakIt(broken);

        Assert.Throws<InvalidDataException>(() => store.Install(Zip(broken), "1.10.4"));

        Assert.Equal("1.10.3", store.ActiveVersion);
        Assert.NotNull(store.GetActive());
        Assert.Equal(new[] { "1.10.3" }, Directory.GetDirectories(Path.Combine(_root, "versions")).Select(Path.GetFileName));
    }

    [Theory]
    [MemberData(nameof(BrokenReleases))]
    public void Install_BrokenZipOfAlreadyActiveVersion_DoesNotWipeWorkingEngine(string _, Action<Dictionary<string, string>> breakIt)
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.3");
        var broken = ValidRelease();
        breakIt(broken);

        Assert.Throws<InvalidDataException>(() => store.Install(Zip(broken), "1.10.3"));

        var active = store.GetActive();
        Assert.NotNull(active);
        Assert.True(File.Exists(active!.WinwsPath));
        Assert.Equal(2, active.Strategies.Count);
    }

    [Fact]
    public void Install_CorruptZip_Rejected()
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.3");

        Assert.Throws<InvalidDataException>(() => store.Install(new MemoryStream(new byte[] { 1, 2, 3, 4 }), "1.10.4"));
        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Fact]
    public void Install_LeftoverFolderFromCrashedInstall_IsReplaced()
    {
        var leftover = Path.Combine(_root, "versions", "1.10.3");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "garbage.txt"), "x");

        var layout = new EngineStore(_root).Install(Zip(ValidRelease()), "1.10.3");

        Assert.True(File.Exists(layout.WinwsPath));
        Assert.False(File.Exists(Path.Combine(leftover, "garbage.txt")));
    }

    [Fact]
    public void Install_OneBrokenStrategy_ExcludedAndReportedOthersKept()
    {
        var release = ValidRelease();
        release["zapret-discord-youtube-1.10.3/general (ALT).bat"] = "echo no winws here";

        var layout = new EngineStore(_root).Install(Zip(release), "1.10.3");

        Assert.Equal(new[] { "general" }, layout.Strategies.Select(s => s.Name));
        Assert.Contains(layout.Problems, p => p.Contains("general (ALT)"));
    }

    [Fact]
    public void Install_StrategyWithUnknownPlaceholder_ExcludedAndReported()
    {
        var release = ValidRelease();
        release["zapret-discord-youtube-1.10.3/general (ALT).bat"] = "start \"z\" \"%BIN%winws.exe\" --wf-tcp=%NewPorts%";

        var layout = new EngineStore(_root).Install(Zip(release), "1.10.3");

        Assert.Equal(new[] { "general" }, layout.Strategies.Select(s => s.Name));
        Assert.Contains(layout.Problems, p => p.Contains("NewPorts"));
    }

    [Fact]
    public void Install_AllStrategiesUseUnknownPlaceholder_ReleaseRejected()
    {
        var release = ValidRelease();
        release["zapret-discord-youtube-1.10.3/general.bat"] = "start \"z\" \"%BIN%winws.exe\" --wf-tcp=%NewPorts%";
        release["zapret-discord-youtube-1.10.3/general (ALT).bat"] = "start \"z\" \"%BIN%winws.exe\" --wf-tcp=%NewPorts%";

        Assert.Throws<InvalidDataException>(() => new EngineStore(_root).Install(Zip(release), "1.10.3"));
    }

    [Fact]
    public void Install_NoIpsetBackup_LeavesIpsetAsShipped()
    {
        var release = ValidRelease();
        release.Remove("zapret-discord-youtube-1.10.3/lists/ipset-all.txt.backup");
        release["zapret-discord-youtube-1.10.3/lists/ipset-all.txt"] = "5.6.7.0/24";

        var layout = new EngineStore(_root).Install(Zip(release), "1.10.3");

        Assert.Equal("5.6.7.0/24", File.ReadAllText(Path.Combine(layout.ListsDir, "ipset-all.txt")).Trim());
    }

    [Theory]
    [InlineData("zapret-discord-youtube-1.10.3/..\\..\\evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    public void Install_ZipSlipVariants_Rejected(string entry)
    {
        var evil = ValidRelease();
        evil[entry] = "x";

        Assert.Throws<InvalidDataException>(() => new EngineStore(_root).Install(Zip(evil), "1.10.3"));
    }

    [Theory]
    [InlineData(@"..\..\x")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData("garbage")]
    public void ActiveVersion_TamperedPointer_Ignored(string pointer)
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.3");
        File.WriteAllText(Path.Combine(_root, "active.txt"), pointer);

        Assert.Null(store.ActiveVersion);
        Assert.Null(store.GetActive());
    }

    [Fact]
    public void CleanupInactive_NoValidPointer_DeletesNothing()
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.2");
        store.Install(Zip(ValidRelease()), "1.10.3");
        File.Delete(Path.Combine(_root, "active.txt"));

        store.CleanupInactive();

        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "versions")).Length);
    }

    [Fact]
    public void Activate_SwitchesBackToInstalledVersion()
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.2");
        store.Install(Zip(ValidRelease()), "1.10.3");

        store.Activate("1.10.2");

        Assert.Equal("1.10.2", store.ActiveVersion);
        Assert.Throws<DirectoryNotFoundException>(() => store.Activate("9.9.9"));
    }

    [Fact]
    public void Install_FullIpsetAlreadyShipped_NotReplacedByBackup()
    {
        var release = ValidRelease();
        release["zapret-discord-youtube-1.10.3/lists/ipset-all.txt"] = "9.9.9.0/24";

        var layout = new EngineStore(_root).Install(Zip(release), "1.10.3");

        Assert.Equal("9.9.9.0/24", File.ReadAllText(Path.Combine(layout.ListsDir, "ipset-all.txt")).Trim());
    }

    [Fact]
    public void Install_TooManyEntries_Rejected()
    {
        var release = ValidRelease();
        for (var i = 0; i <= EngineStore.MaxEntries; i++) release[$"zapret-discord-youtube-1.10.3/junk/{i}.txt"] = "";

        Assert.Throws<InvalidDataException>(() => new EngineStore(_root).Install(Zip(release), "1.10.3"));
    }

    [Fact]
    public void Install_RootPathWithSpacesAndCyrillic_Works()
    {
        var root = Path.Combine(_root, "Мои файлы", "zapret hub");

        var layout = new EngineStore(root).Install(Zip(ValidRelease()), "1.10.3");

        Assert.True(File.Exists(layout.WinwsPath));
    }

    [Fact]
    public void CleanupInactive_LockedOldVersion_SkippedThenRemovedWhenReleased()
    {
        var store = new EngineStore(_root);
        var old = store.Install(Zip(ValidRelease()), "1.10.2");
        store.Install(Zip(ValidRelease()), "1.10.3");

        using (new FileStream(old.WinwsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.CleanupInactive();
            Assert.True(File.Exists(old.WinwsPath));
        }

        store.CleanupInactive();
        Assert.False(Directory.Exists(old.RootDir));
        Assert.Equal("1.10.3", store.ActiveVersion);
    }

    [Fact]
    public void Install_NoParsableStrategy_Rejected()
    {
        var broken = ValidRelease();
        broken["zapret-discord-youtube-1.10.3/general.bat"] = "echo changed format";
        broken["zapret-discord-youtube-1.10.3/general (ALT).bat"] = "echo changed format";

        Assert.Throws<InvalidDataException>(() => new EngineStore(_root).Install(Zip(broken), "1.10.3"));
    }

    [Fact]
    public void Install_ZipSlipEntry_Rejected()
    {
        var evil = ValidRelease();
        evil["zapret-discord-youtube-1.10.3/../../evil.txt"] = "x";

        Assert.Throws<InvalidDataException>(() => new EngineStore(_root).Install(Zip(evil), "1.10.3"));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void Install_InvalidVersionString_Rejected()
    {
        Assert.Throws<FormatException>(() => new EngineStore(_root).Install(Zip(ValidRelease()), "..\\x"));
    }

    [Fact]
    public void ActiveEngine_SurvivesNewStoreInstance()
    {
        new EngineStore(_root).Install(Zip(ValidRelease()), "1.10.3");

        var reopened = new EngineStore(_root);

        Assert.Equal("1.10.3", reopened.ActiveVersion);
        Assert.NotNull(reopened.GetActive());
    }

    [Fact]
    public void GetActive_NothingInstalled_ReturnsNull()
    {
        Assert.Null(new EngineStore(_root).GetActive());
    }

    [Fact]
    public void CleanupInactive_RemovesOldVersionsKeepsActive()
    {
        var store = new EngineStore(_root);
        store.Install(Zip(ValidRelease()), "1.10.2");
        store.Install(Zip(ValidRelease()), "1.10.3");

        store.CleanupInactive();

        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.10.2")));
        Assert.True(Directory.Exists(Path.Combine(_root, "versions", "1.10.3")));
    }
}
