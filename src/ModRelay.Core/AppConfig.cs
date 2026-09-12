namespace ModRelay.Core;

/// <summary>
/// Everything the user can change. Defaults are what a fresh install should do.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Notifications for failed imports, damaged archives, and unavailable services.</summary>
    public bool ShowErrorNotifications { get; set; } = true;

    /// <summary>Extract every mod inside an archive instead of asking which one.</summary>
    public bool ExtractAllMods { get; set; }

    /// <summary>Register under HKCU\...\Run.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Run pre-Dawntrail mods through TexTools ConsoleTools /upgrade before installing.</summary>
    public bool AutoUpgradeToDawntrail { get; set; } = true;

    /// <summary>Associate supported mod packages with this app (per user, HKCU).</summary>
    public bool AssociateFileTypes { get; set; }

    /// <summary>Use ModRelay's dark color palette.</summary>
    public bool DarkMode { get; set; } = true;

    /// <summary>Check the official release feed after startup.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    public List<string> WatchFolders { get; set; } = [];

    /// <summary>Full path to TexTools' ConsoleTools.exe. Empty means "not set up".</summary>
    public string TexToolsConsolePath { get; set; } = string.Empty;

    public int PenumbraTimeoutSeconds { get; set; } = 60;

    /// <summary>Creates an independent working copy for immediate UI updates.</summary>
    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.WatchFolders = [.. WatchFolders];
        return copy;
    }
}
