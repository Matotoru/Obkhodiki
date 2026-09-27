using System.IO.Compression;
using System.Text;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.Core.Tests.Updates;

public sealed class UpdatePackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("upd").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(text);
            }
        }
        ms.Position = 0;
        return ms;
    }

    // A 0.5 archive: the single-file app, WPF's native libraries and placeholders that 0.4.x updaters require.
    [Fact]
    public void Extract_SingleFileWithLegacyPlaceholders_KeepOnlyLeavesTheApp()
    {
        var dir = Path.Combine(_root, "new");
        var app = new[] { "Obkhodiki.exe", "wpfgfx_cor3.dll" };
        var legacy = new[] { "Obkhodiki.dll", "Obkhodiki.deps.json", "Wpf.Ui.dll" };
        using var zip = Zip(app.Concat(legacy).Append("LICENSE").Select(n => (n, "x")).ToArray());

        // What a 0.4.x updater checks: its old file list is there.
        UpdatePackage.Extract(zip, dir, legacy, 1024);
        UpdatePackage.KeepOnly(dir, app);

        Assert.Equal(app.Order(StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Obkhodiki.exe", true)]
    [InlineData("Wpf.Ui.Abstractions.dll", true)]
    [InlineData("Obkhodiki.deps.json", true)]
    [InlineData("New.Dependency.dll", true)]
    [InlineData("../evil.dll", false)]
    [InlineData("sub/evil.dll", false)]
    [InlineData("sub\\evil.dll", false)]
    [InlineData("C:evil.dll", false)]
    [InlineData("a.dll:stream", false)]
    [InlineData("readme.txt", false)]
    [InlineData("x..dll", false)]
    public void Names(string name, bool allowed) => Assert.Equal(allowed, UpdatePackage.IsAllowedName(name));

    [Fact]
    public void Extract_TakesRootProgramFilesIncludingNewOnes_SkipsTheRest()
    {
        var dir = Path.Combine(_root, "x");
        UpdatePackage.Extract(Zip(("App.exe", "e"), ("New.dll", "n"), ("notes.txt", "t"), ("sub/Other.dll", "o"), ("../Up.dll", "u")),
            dir, new[] { "App.exe" }, 1024);

        Assert.Equal(new[] { "App.exe", "New.dll" }, Directory.GetFiles(dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Extract_MissingRequiredOrDuplicateOrOversized_Rejected()
    {
        var dir = Path.Combine(_root, "x");
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(Zip(("Other.dll", "o")), dir, new[] { "App.exe" }, 1024));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(Zip(("App.exe", "a"), ("App.exe", "b")), dir, new[] { "App.exe" }, 1024));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(Zip(("App.exe", new string('x', 2000))), dir, new[] { "App.exe" }, 1024));
    }

    [Fact]
    public void Swap_ReplacesAndAdds_RestoreUndoesBoth()
    {
        var source = Dir("src");
        var target = Dir("target");
        var backup = Path.Combine(_root, "backup");
        File.WriteAllText(Path.Combine(source, "App.exe"), "new");
        File.WriteAllText(Path.Combine(source, "New.dll"), "new");
        File.WriteAllText(Path.Combine(target, "App.exe"), "old");
        File.WriteAllText(Path.Combine(target, "Keep.dll"), "old");

        var swap = UpdatePackage.Swap(source, target, backup);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "App.exe")));
        Assert.True(File.Exists(Path.Combine(target, "New.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Keep.dll")));

        Assert.True(UpdatePackage.Restore(target, backup, swap));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "App.exe")));
        Assert.False(File.Exists(Path.Combine(target, "New.dll")));
    }

    [Fact]
    public void Swap_FailureOnALaterFile_RollsBackEverything()
    {
        var source = Dir("src");
        var target = Dir("target");
        foreach (var n in new[] { "A.dll", "B.dll", "C.dll" })
        {
            File.WriteAllText(Path.Combine(source, n), "new");
            File.WriteAllText(Path.Combine(target, n), "old");
        }
        var calls = 0;
        void FlakyCopy(string from, string to)
        {
            if (++calls == 3) throw new IOException("locked");
            File.Copy(from, to, overwrite: true);
        }

        Assert.Throws<IOException>(() => UpdatePackage.Swap(source, target, Path.Combine(_root, "backup"), FlakyCopy));

        Assert.All(Directory.GetFiles(target), f => Assert.Equal("old", File.ReadAllText(f)));
    }
}
