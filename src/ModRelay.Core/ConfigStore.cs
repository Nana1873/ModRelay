using System.Text.Json;

namespace ModRelay.Core;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as JSON.
/// The directory is created on save so settings persist between portable launches.
/// </summary>
public sealed class ConfigStore(string configFilePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();

    public string FilePath { get; } = configFilePath;

    public ConfigStore() : this(AppPaths.ConfigFile)
    {
    }

    public AppConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                Log.Info($"No config at {FilePath}; starting with defaults.");
                return WithDefaults(new AppConfig(), addDefaultWatchFolder: true);
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (config is not null)
                    return WithDefaults(config, addDefaultWatchFolder: false);

                Log.Warn($"Config at {FilePath} deserialised to null; using defaults.");
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read config at {FilePath}; using defaults.", ex);
                BackupBrokenFile();
            }

            return WithDefaults(new AppConfig(), addDefaultWatchFolder: true);
        }
    }

    /// <summary>
    /// Writes to a temp file first and then moves it into place, so a crash
    /// mid-write cannot leave a truncated config behind.
    /// </summary>
    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, FilePath, overwrite: true);

            Log.Debug($"Config saved to {FilePath}");
        }
    }

    private static AppConfig WithDefaults(AppConfig config, bool addDefaultWatchFolder)
    {
        config.WatchFolders ??= [];

        if (addDefaultWatchFolder && config.WatchFolders.Count == 0)
        {
            var downloads = DefaultDownloadFolder();
            if (downloads is not null)
                config.WatchFolders.Add(downloads);
        }

        config.WatchFolders = config.WatchFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.PenumbraTimeoutSeconds <= 0)
            config.PenumbraTimeoutSeconds = 60;

        return config;
    }

    private static string? DefaultDownloadFolder()
    {
        // No SpecialFolder for Downloads; the profile-relative path is the reliable fallback.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
            return null;

        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : null;
    }

    private void BackupBrokenFile()
    {
        try
        {
            var broken = FilePath + ".broken";
            File.Copy(FilePath, broken, overwrite: true);
            Log.Info($"Unreadable config copied to {broken}");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not back up the unreadable config.", ex);
        }
    }
}
