namespace ModRelay.Core;

/// <summary>
/// Everything the pipeline needs from the UI. Keeps <see cref="ModPipeline"/> free of WinForms
/// so it can be tested without a message loop.
/// </summary>
public interface IUserInteraction
{
    /// <summary>Shows a small non-blocking window while an archive is inspected or extracted.</summary>
    Task BeginArchiveProgressAsync(string archiveName, string message);

    /// <summary>Updates the currently visible archive operation.</summary>
    void UpdateArchiveProgress(string message);

    /// <summary>Closes the archive progress window before a decision or notification is shown.</summary>
    Task EndArchiveProgressAsync();

    /// <summary>
    /// Asks which mods to take out of an archive. Return an empty list to skip the archive.
    /// </summary>
    Task<IReadOnlyList<string>> SelectArchiveEntriesAsync(string archivePath, IReadOnlyList<ArchiveEntryInfo> entries);

    /// <summary>
    /// The Dawntrail upgrade did not happen. Install the untouched file anyway?
    /// </summary>
    Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result);

    void Notify(string title, string message, bool isError = false);

    /// <summary>Short line for the tray tooltip - conversions take minutes.</summary>
    void Status(string message);
}
