using ModRelay.Core;

namespace ModRelay.App;

internal static class TrayStatusText
{
    internal static string Watching(IReadOnlyList<WatchFolderStatus> folders, bool paused, bool setupPending)
    {
        if (setupPending)
            return "Complete setup to start watching";

        var active = folders.Count(folder => folder.IsWatching);
        var failed = folders.Count - active;
        var status = active == 0 ? "No folders watched"
            : paused ? "Watching paused"
            : $"Watching {active} folder{(active == 1 ? "" : "s")}";
        var warnings = folders.Count(folder => folder.IsWatching && folder.Problem is not null);
        if (failed > 0) status += $" · {failed} unavailable";
        if (warnings > 0) status += $" · {warnings} need checking";
        return status;
    }

    internal static string Queue(int queued, int pending) => pending == 0
        ? $"{queued} file{(queued == 1 ? "" : "s")} queued"
        : $"{queued} queued · {pending} waiting for Penumbra";
}
