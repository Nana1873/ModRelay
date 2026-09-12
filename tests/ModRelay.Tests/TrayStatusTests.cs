using ModRelay.App;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class TrayStatusTests
{
    [Fact]
    public void MissingFolders_NeverReportReadyOrWatching()
    {
        var folders = new[] { new WatchFolderStatus(@"C:\missing", false, "Missing") };
        Assert.Equal("No folders watched · 1 unavailable", TrayStatusText.Watching(folders, false, false));
    }

    [Fact]
    public void MixedFolderHealth_ReportsTheActiveCountAndTheProblem()
    {
        WatchFolderStatus[] folders = [new(@"C:\downloads", true, null), new(@"C:\missing", false, "Missing")];
        Assert.Equal("Watching 1 folder · 1 unavailable", TrayStatusText.Watching(folders, false, false));
        Assert.Equal("Watching paused · 1 unavailable", TrayStatusText.Watching(folders, true, false));
        Assert.Equal("Watching 1 folder · 1 need checking",
            TrayStatusText.Watching([new(@"C:\downloads", true, "Some downloads could not be checked.")], false, false));
    }

    [Fact]
    public void QueueText_DistinguishesNewWorkFromWaitingForPenumbra()
    {
        Assert.Equal("3 queued · 2 waiting for Penumbra", TrayStatusText.Queue(3, 2));
        Assert.Equal("1 file queued", TrayStatusText.Queue(1, 0));
    }
}
