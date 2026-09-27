using Obkhodiki.Core.Strategies;

namespace Obkhodiki.Core.Tests.Strategies;

public sealed class UserListsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zh-lists-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void EnsureDefaults_CreatesNonEmptyFilesWinwsCanLoad()
    {
        UserLists.EnsureDefaults(_dir);

        foreach (var name in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
        {
            var path = Path.Combine(_dir, name);
            Assert.True(File.Exists(path), name);
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)), name);
        }
    }

    [Fact]
    public void EveryUserListReferencedByRealStrategies_IsCreatedByEnsureDefaults()
    {
        var strategies = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "general*.bat")
            .Select(f => BatStrategyParser.Parse(Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();

        UserLists.EnsureDefaults(_dir);

        var referenced = UserLists.ReferencedFiles(strategies);
        Assert.NotEmpty(referenced);
        Assert.All(referenced, name => Assert.True(File.Exists(Path.Combine(_dir, name)), name));
    }

    [Fact]
    public void EnsureReferenced_NewUserListFromFutureRelease_CreatedWithMatchingPlaceholder()
    {
        var strategy = new StrategyDefinition("new", new[] { "--ipset=%LISTS%ipset-game-user.txt", "--hostlist=%LISTS%list-games-user.txt" });

        UserLists.EnsureReferenced(new[] { strategy }, _dir);

        Assert.Contains("203.0.113.113", File.ReadAllText(Path.Combine(_dir, "ipset-game-user.txt")));
        Assert.Contains("domain.example.abc", File.ReadAllText(Path.Combine(_dir, "list-games-user.txt")));
    }

    [Fact]
    public void EnsureReferenced_PathEscapeAttempt_Ignored()
    {
        var strategy = new StrategyDefinition("evil", new[] { @"--ipset=%LISTS%..\..\evil-user.txt" });

        UserLists.EnsureReferenced(new[] { strategy }, _dir);

        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir, "..", "..", "evil-user.txt"))));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void EnsureDefaults_NeverOverwritesUserEdits()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "list-general-user.txt");
        File.WriteAllText(path, "wardogs.example");

        UserLists.EnsureDefaults(_dir);

        Assert.Equal("wardogs.example", File.ReadAllText(path));
    }
}
