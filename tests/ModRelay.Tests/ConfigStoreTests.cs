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
            ShowErrorNotifications = false,
            AutoUpgradeToDawntrail = true,
            WatchFolders = [temp.Path]
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.True(File.Exists(configPath));
        Assert.False(loaded.ShowErrorNotifications);
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
        Assert.Empty(loaded.WatchFolders);
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Load_NullJson_UsesSafeDefaultsAndKeepsBackup()
    {
        using var temp = new TestDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, "null");

        var loaded = new ConfigStore(path).Load();

        Assert.Empty(loaded.WatchFolders);
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Load_LegacyNotificationAndAutomationFields_AreIgnoredWithoutLosingFolders()
    {
        using var temp = new TestDirectory();
        var path = temp.File("config.json");
        var watched = temp.File("watched");
        Directory.CreateDirectory(watched);
        File.WriteAllText(path, $$"""
            {
              "ShowNotifications": false,
              "ShowTrayNotifications": false,
              "PlayNotificationSounds": false,
              "AutoDeleteMods": false,
              "AutoForwardToPenumbra": true,
              "InstallOriginalWhenUpgradeFails": true,
              "WatchFolders": [{{System.Text.Json.JsonSerializer.Serialize(watched)}}]
            }
            """);

        var loaded = new ConfigStore(path).Load();

        Assert.Equal([watched], loaded.WatchFolders);
    }

    [Fact]
    public void Load_LegacyDisabledForwarding_StopsWatchingAndPreservesFirstBackup()
    {
        using var temp = new TestDirectory();
        var path = temp.File("settings.json");
        var backup = path + ".before-simplification";
        var watched = temp.File("watched");
        var original = $$"""
            {
              "AutoForwardToPenumbra": false,
              "DarkMode": false,
              "ShowErrorNotifications": false,
              "WatchFolders": [{{System.Text.Json.JsonSerializer.Serialize(watched)}}]
            }
            """;
        File.WriteAllText(path, original);

        var loaded = new ConfigStore(path).Load();

        Assert.Empty(loaded.WatchFolders);
        Assert.False(loaded.DarkMode);
        Assert.False(loaded.ShowErrorNotifications);
        Assert.Equal(original, File.ReadAllText(backup));

        File.WriteAllText(path, """
            {
              "AutoForwardToPenumbra": false,
              "WatchFolders": ["changed"]
            }
            """);
        _ = new ConfigStore(path).Load();

        Assert.Equal(original, File.ReadAllText(backup));
    }
}
