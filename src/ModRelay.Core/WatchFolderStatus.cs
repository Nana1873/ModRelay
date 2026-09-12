namespace ModRelay.Core;

/// <summary>Current readiness of one configured download folder.</summary>
public sealed record WatchFolderStatus(string Path, bool IsWatching, string? Problem);
