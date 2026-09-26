using ZapretHub.Core.Engine;

namespace ZapretHub.Core.Tests.Engine;

public class ConflictDetectorTests
{
    private static readonly string[] NoServices = Array.Empty<string>();

    [Fact]
    public void Find_ForeignWinwsAndGoodbyeDpi_Reported()
    {
        var running = new[] { new ProcessInfo("winws", 10), new ProcessInfo("GoodbyeDPI", 11), new ProcessInfo("chrome", 12) };

        var report = ConflictDetector.Find(running, NoServices, ownProcessId: null);

        Assert.Equal(new[] { 10, 11 }, report.Processes.Select(c => c.Id));
        Assert.True(report.Any);
    }

    [Fact]
    public void Find_OwnWinws_NotAConflict()
    {
        var running = new[] { new ProcessInfo("winws", 10), new ProcessInfo("winws", 20) };

        var report = ConflictDetector.Find(running, NoServices, ownProcessId: 20);

        Assert.Equal(new[] { 10 }, report.Processes.Select(c => c.Id));
    }

    [Theory]
    [InlineData("zapret")]
    [InlineData("GoodbyeDPI")]
    [InlineData("ZAPRET")]
    public void Find_BypassServiceRunning_Reported(string service)
    {
        var report = ConflictDetector.Find(Array.Empty<ProcessInfo>(), new[] { service, "Spooler" }, null);

        Assert.Equal(new[] { service }, report.Services);
        Assert.True(report.Any);
    }

    [Fact]
    public void Find_WinDivertDriverServiceAlone_NotAConflict()
    {
        var report = ConflictDetector.Find(Array.Empty<ProcessInfo>(), new[] { "WinDivert" }, null);

        Assert.False(report.Any);
    }

    [Fact]
    public void Find_NothingRelevant_Empty()
    {
        Assert.False(ConflictDetector.Find(new[] { new ProcessInfo("steam", 1) }, NoServices, null).Any);
    }
}
