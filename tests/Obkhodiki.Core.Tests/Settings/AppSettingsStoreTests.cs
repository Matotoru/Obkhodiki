using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Strategies;

namespace Obkhodiki.Core.Tests.Settings;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zh-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var s = new AppSettingsStore(FilePath).Load();

        Assert.Null(s.SelectedStrategy);
        Assert.Equal(GameFilterMode.Disabled, s.GameFilter);
        Assert.True(s.CheckUpdatesOnStart);
        Assert.False(s.EnableOnStart);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new AppSettingsStore(FilePath);
        store.Save(new AppSettings
        {
            SelectedStrategy = "general (ALT)",
            GameFilter = GameFilterMode.Udp,
            GameTcpRange = "27000-27100",
            GameUdpRange = "7000-8000",
            EnableOnStart = true,
            CheckUpdatesOnStart = false,
        });

        var loaded = store.Load();

        Assert.Equal("general (ALT)", loaded.SelectedStrategy);
        Assert.Equal(GameFilterMode.Udp, loaded.GameFilter);
        Assert.Equal("27000-27100", loaded.GameTcpRange);
        Assert.Equal("7000-8000", loaded.GameUdpRange);
        Assert.True(loaded.EnableOnStart);
        Assert.False(loaded.CheckUpdatesOnStart);
    }

    private AppSettings LoadJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
        return new AppSettingsStore(FilePath).Load();
    }

    // Must fall back to Disabled, never All: All would silently intercept every port 1024-65535.
    [Fact]
    public void Load_UnknownGameFilterNumber_FallsBackToDisabledKeepsOtherFields()
    {
        var s = LoadJson("{ \"GameFilter\": 99, \"SelectedStrategy\": \"x\" }");

        Assert.Equal(GameFilterMode.Disabled, s.GameFilter);
        Assert.Equal("x", s.SelectedStrategy);
    }

    [Fact]
    public void Load_InvalidTcpRange_ResetOnlyThatField()
    {
        var s = LoadJson("{ \"GameTcpRange\": \"abc\", \"GameUdpRange\": \"7000-8000\" }");

        Assert.Equal(GameFilterOptions.DefaultRange, s.GameTcpRange);
        Assert.Equal("7000-8000", s.GameUdpRange);
    }

    [Fact]
    public void Load_NullUdpRange_ResetOnlyThatField()
    {
        var s = LoadJson("{ \"GameTcpRange\": \"27000-27100\", \"GameUdpRange\": null }");

        Assert.Equal("27000-27100", s.GameTcpRange);
        Assert.Equal(GameFilterOptions.DefaultRange, s.GameUdpRange);
    }

    [Fact]
    public void GameProfiles_RoundTripAndAreSanitizedOnLoad()
    {
        var store = new AppSettingsStore(FilePath);
        store.Save(new AppSettings
        {
            GameProfiles =
            {
                new() { Id = "war-dogs", Name = "War Dogs", Enabled = true, ProcessName = "wardogs.exe", UdpPorts = "7777-7800" },
                new() { Id = "../evil", Enabled = true },
            },
        });

        var loaded = store.Load();

        var profile = Assert.Single(loaded.GameProfiles);
        Assert.Equal("War Dogs", profile.Name);
        Assert.True(profile.Enabled);
        Assert.Equal("wardogs.exe", profile.ProcessName);
        Assert.Equal("7777-7800", profile.UdpPorts);
    }

    [Fact]
    public void Load_GameProfilesNull_BecomesEmptyList()
    {
        Assert.Empty(LoadJson("{ \"GameProfiles\": null }").GameProfiles);
    }

    [Fact]
    public void Load_FileLockedByAnotherProcess_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ \"SelectedStrategy\": \"x\" }");
        using var _ = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var s = new AppSettingsStore(FilePath).Load();

        Assert.Null(s.SelectedStrategy);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ \"GameFilter\": \"Everything\" }")]
    public void Load_UnusableFile_ReturnsDefaultsBacksUpOriginalAndStaysWritable(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, content);
        var store = new AppSettingsStore(FilePath);

        var s = store.Load();

        Assert.Null(s.SelectedStrategy);
        Assert.Equal(GameFilterMode.Disabled, s.GameFilter);
        Assert.Equal(content, File.ReadAllText(FilePath + ".corrupt"));

        store.Save(new AppSettings { SelectedStrategy = "general" });
        Assert.Equal("general", store.Load().SelectedStrategy);
    }
}
