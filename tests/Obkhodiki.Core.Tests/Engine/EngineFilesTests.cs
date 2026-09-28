using Obkhodiki.Core.Engine;

namespace Obkhodiki.Core.Tests.Engine;

public sealed class EngineFilesTests : IDisposable
{
    private readonly string _bin = Path.Combine(Directory.CreateTempSubdirectory("eng").FullName, "engine", "versions", "1.0", "bin");

    public EngineFilesTests() => Directory.CreateDirectory(_bin);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_bin))))!, recursive: true);

    [Fact]
    public void Missing_ListsQuarantinedFiles()
    {
        File.WriteAllText(Path.Combine(_bin, "winws.exe"), "x");
        File.WriteAllText(Path.Combine(_bin, "cygwin1.dll"), "x");

        Assert.Equal(new[] { "WinDivert.dll", "WinDivert64.sys" }, EngineFiles.Missing(_bin));
        var message = EngineFiles.MissingMessage(EngineFiles.Missing(_bin), _bin);
        Assert.Contains("антивирус", message);
        Assert.Contains(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_bin)))!, message);
    }

    [Theory]
    [InlineData("winws.exe exited with code 2: error opening filter: WinDivert.dll not found", true)]
    [InlineData("windivert: error opening filter: 577", true)]
    [InlineData("winws.exe exited with code 1: bad argument --dpi-desync-foo", false)]
    public void StartFailure_HintOnlyForDriverProblems(string error, bool expectHint)
    {
        Assert.Equal(expectHint, EngineFiles.ExplainStartFailure(error, _bin) is not null);
    }
}
