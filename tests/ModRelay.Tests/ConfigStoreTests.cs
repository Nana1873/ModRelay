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
            PenumbraPort = 42123,
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
        Assert.Equal(42123, loaded.PenumbraPort);
        Assert.Equal([temp.Path], loaded.WatchFolders);
    }

    [Fact]
    public void Load_BrokenJson_ReturnsUsableDefaultsAndKeepsBackup()
    {
        using var temp = new TestDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, "{ definitely not json");

        var loaded = new ConfigStore(path).Load();

        Assert.Equal(42069, loaded.PenumbraPort);
        Assert.NotNull(loaded.WatchFolders);
        Assert.True(File.Exists(path + ".broken"));
    }
}
