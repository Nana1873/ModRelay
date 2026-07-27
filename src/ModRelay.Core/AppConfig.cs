namespace ModRelay.Core;

/// <summary>
/// Everything the user can change. Defaults are what a fresh install should do.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Notification after a mod was handed to Penumbra successfully.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Notifications for failed imports, damaged archives, and unavailable services.</summary>
    public bool ShowErrorNotifications { get; set; } = true;

    /// <summary>Tell the user that closing the settings leaves ModRelay in the tray.</summary>
    public bool ShowTrayNotifications { get; set; } = true;

    /// <summary>Ask Windows to play its standard sound for ModRelay notifications.</summary>
    public bool PlayNotificationSounds { get; set; } = true;

    /// <summary>Send finished mods to Penumbra automatically.</summary>
    public bool AutoForwardToPenumbra { get; set; } = true;

    /// <summary>Extract every mod inside an archive instead of asking which one.</summary>
    public bool ExtractAllMods { get; set; }

    /// <summary>Register under HKCU\...\Run.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Delete source archives and intermediate files once they are no longer needed.</summary>
    public bool AutoDeleteMods { get; set; } = true;

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

    public int PenumbraPort { get; set; } = 42069;

    public int PenumbraTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// When the upgrade fails, install the untouched original anyway.
    /// Off by default - silently installing an un-upgraded mod is the exact
    /// behaviour that made the predecessor look broken.
    /// </summary>
    public bool InstallOriginalWhenUpgradeFails { get; set; }

    /// <summary>Creates an independent working copy for immediate UI updates.</summary>
    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.WatchFolders = [.. WatchFolders];
        return copy;
    }
}
