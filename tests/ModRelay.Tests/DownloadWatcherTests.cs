using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class DownloadWatcherTests
{
    [Fact]
    public async Task RepeatedFileSystemEvents_ForUnchangedFileEmitOnlyOnce()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        watcher.FileReady += path =>
        {
            Interlocked.Increment(ref count);
            ready.TrySetResult(path);
        };
        watcher.Start([temp.Path]);

        var package = temp.File("mod.pmp");
        await File.WriteAllTextAsync(package, "package");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // FileSystemWatcher commonly delivers several Created/Changed events for one write.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(1, Volatile.Read(ref count));
    }
}
