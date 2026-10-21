using System.Security.Cryptography;
using System.Text;
using Obkhodiki.Core.Engine;

namespace Obkhodiki.Core.Tests.Engine;

public sealed class DriverHidingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hide").FullName;
    private readonly string _bin;
    private readonly string _hidden;
    private readonly string _patched;

    public DriverHidingTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        _hidden = Path.Combine(_root, "hidden");
        _patched = Path.Combine(_root, "patched.dll");
        File.WriteAllText(Path.Combine(_bin, "winws.exe"), "winws");
        File.WriteAllText(Path.Combine(_bin, "cygwin1.dll"), "cygwin");
        File.WriteAllText(Path.Combine(_bin, "WinDivert.dll"), "original dll");
        File.WriteAllText(Path.Combine(_bin, "WinDivert64.sys"), "driver");
        File.WriteAllText(Path.Combine(_bin, "quic_initial_www_google_com.bin"), "fake");
        File.WriteAllText(_patched, "patched dll");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private void Prepare() => DriverHiding.Prepare(_bin, _hidden, _patched, Hash("driver"), Hash("patched dll"));

    [Fact]
    public void Prepare_PutsPatchedDllAndRenamedDriverNextToWinws()
    {
        Prepare();

        Assert.Equal(new[] { "Monkey64.sys", "WinDivert.dll", "cygwin1.dll", "winws.exe" },
            Directory.GetFiles(_hidden).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("patched dll", File.ReadAllText(Path.Combine(_hidden, "WinDivert.dll")));
        Assert.Equal("driver", File.ReadAllText(Path.Combine(_hidden, "Monkey64.sys")));
        Assert.Equal("original dll", File.ReadAllText(Path.Combine(_bin, "WinDivert.dll")));
    }

    [Fact]
    public void Prepare_Again_LeavesLockedUnchangedFilesAlone()
    {
        Prepare();
        // A running winws keeps its exe open; a repeated prepare must not try to overwrite it.
        using var locked = new FileStream(Path.Combine(_hidden, "winws.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Prepare();
    }

    [Fact]
    public void Prepare_RefreshesChangedEngineFiles()
    {
        Prepare();
        File.WriteAllText(Path.Combine(_bin, "winws.exe"), "winws v2");

        Prepare();

        Assert.Equal("winws v2", File.ReadAllText(Path.Combine(_hidden, "winws.exe")));
    }

    [Fact]
    public void Prepare_RejectsAlteredPatchedDll()
    {
        File.WriteAllText(_patched, "something else");

        var ex = Assert.Throws<InvalidOperationException>(Prepare);
        Assert.Contains("антивирус", ex.Message);
        Assert.False(Directory.Exists(_hidden));
    }

    [Fact]
    public void Prepare_RejectsMissingPatchedDll()
    {
        File.Delete(_patched);

        Assert.Throws<InvalidOperationException>(Prepare);
    }

    [Fact]
    public void Prepare_RejectsDriverThePatchedDllWasNotBuiltFor()
    {
        File.WriteAllText(Path.Combine(_bin, "WinDivert64.sys"), "newer driver");

        var ex = Assert.Throws<InvalidOperationException>(Prepare);
        Assert.Equal(DriverHiding.UnsupportedDriverMessage, ex.Message);
        Assert.False(DriverHiding.HasSupportedDriver(_bin, Hash("driver")));
    }

    [Theory]
    [InlineData(true, "WinDivert")]
    [InlineData(false, "Monkey")]
    public void ServiceToStop_IsTheOtherModesDriver(bool hide, string expected) =>
        Assert.Equal(expected, DriverHiding.ServiceToStop(hide));

    [Fact]
    public void IsPatchedDll_AcceptsOnlyThePinnedFile() =>
        Assert.False(DriverHiding.IsPatchedDll(Encoding.UTF8.GetBytes("patched dll")));

    [Fact]
    public void Downloads_ComeFromBolVanOverHttps() =>
        Assert.All(DriverHiding.PatchedDllUrls, u =>
        {
            Assert.Equal(Uri.UriSchemeHttps, u.Scheme);
            Assert.StartsWith("/bol-van/zapret-win-bundle/", u.AbsolutePath);
        });
}
