using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void Save_CreatesMissingDirectory_AndRoundTripsValues()
    {
        using var temp = new TestDirectory();
        var configPath = System.IO.Path.Combine(temp.Path, "missing", "config.json");
        var store = new ConfigStore(configPath);
        var config = new AppConfig
        {
            ShowNotifications = false,
            ShowErrorNotifications = false,
            ShowTrayNotifications = false,
            PlayNotificationSounds = false,
            AutoUpgradeToDawntrail = true,
            WatchFolders = [temp.Path]
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.True(File.Exists(configPath));
        Assert.False(loaded.ShowNotifications);
        Assert.False(loaded.ShowErrorNotifications);
        Assert.False(loaded.ShowTrayNotifications);
        Assert.False(loaded.PlayNotificationSounds);
        Assert.True(loaded.AutoUpgradeToDawntrail);
        Assert.Equal([temp.Path], loaded.WatchFolders);
    }

    [Fact]
    public void Save_AndLoad_PreservesAnExplicitlyEmptyWatchList()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.File("settings.json"));
        store.Save(new AppConfig { WatchFolders = [] });

        var loaded = store.Load();

        Assert.Empty(loaded.WatchFolders);
    }

    [Fact]
    public void Load_BrokenJson_ReturnsUsableDefaultsAndKeepsBackup()
    {
        using var temp = new TestDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, "{ definitely not json");

        var loaded = new ConfigStore(path).Load();

        Assert.Equal(60, loaded.PenumbraTimeoutSeconds);
        Assert.NotNull(loaded.WatchFolders);
        Assert.True(File.Exists(path + ".broken"));
    }
}
