namespace ModRelay.Core;

/// <summary>
/// Every path the portable app writes to. Personal paths never need to leave
/// the folder containing ModRelay.exe.
/// </summary>
public static class AppPaths
{
    public const string AppName = "ModRelay";

    public static string ApplicationDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary>Logs and the retry queue live away from the user-editable settings file.</summary>
    public static string DataDirectory { get; } = Path.Combine(ApplicationDirectory, "data");

    public static string ConfigFile => Path.Combine(ApplicationDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Mods waiting for Penumbra to become reachable.</summary>
    public static string PendingQueueFile => Path.Combine(DataDirectory, "pending.json");

}
