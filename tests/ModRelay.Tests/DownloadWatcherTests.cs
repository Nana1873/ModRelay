using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class DownloadWatcherTests
{
    [Fact]
    public void Start_ValidFolderReportsWatching()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher();

        watcher.Start([temp.Path]);

        var status = Assert.Single(watcher.FolderStatuses);
        Assert.Equal(temp.Path, status.Path);
        Assert.True(status.IsWatching);
        Assert.Null(status.Problem);
    }

    [Fact]
    public void Start_MissingFolderReportsFriendlyProblem()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher();
        var missing = temp.File("missing");

        watcher.Start([missing]);

        var status = Assert.Single(watcher.FolderStatuses);
        Assert.Equal(missing, status.Path);
        Assert.False(status.IsWatching);
        Assert.Equal("Folder does not exist or is unavailable.", status.Problem);
    }

    [Fact]
    public void Start_AllFailedReportsEveryConfiguredFolder()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher();
        var missingA = temp.File("missing-a");
        var missingB = temp.File("missing-b");

        watcher.Start([missingA, missingB]);

        Assert.Equal([missingA, missingB], watcher.FolderStatuses.Select(status => status.Path));
        Assert.All(watcher.FolderStatuses, status => Assert.False(status.IsWatching));
    }

    [Fact]
    public void Start_MixedFoldersPreservesConfiguredOrderAndActualState()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher();
        var missing = temp.File("missing");

        watcher.Start([missing, temp.Path]);

        Assert.Collection(
            watcher.FolderStatuses,
            status =>
            {
                Assert.Equal(missing, status.Path);
                Assert.False(status.IsWatching);
            },
            status =>
            {
                Assert.Equal(temp.Path, status.Path);
                Assert.True(status.IsWatching);
            });
    }

    [Fact]
    public void Start_WatcherCreationFailureReportsUnhealthyFolder()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher(_ => throw new IOException("simulated"));

        watcher.Start([temp.Path]);

        var status = Assert.Single(watcher.FolderStatuses);
        Assert.False(status.IsWatching);
        Assert.Equal("Windows could not watch this folder.", status.Problem);
    }

    [Fact]
    public void StopAndRestart_PublishOnlyCompleteSnapshots()
    {
        using var temp = new TestDirectory();
        using var watcher = new DownloadWatcher();
        var missing = temp.File("missing");
        var snapshots = new List<WatchFolderStatus[]>();
        watcher.FolderStatusesChanged += statuses => snapshots.Add([.. statuses]);

        watcher.Start([temp.Path]);
        watcher.Stop();
        watcher.Start([temp.Path, missing]);

        Assert.Equal(3, snapshots.Count);
        Assert.Single(snapshots[0]);
        Assert.True(snapshots[0][0].IsWatching);
        Assert.Empty(snapshots[1]);
        Assert.Equal(2, snapshots[2].Length);
        Assert.True(snapshots[2][0].IsWatching);
        Assert.False(snapshots[2][1].IsWatching);
    }

    [Fact]
    public async Task WatcherError_AttemptsOneRestartAndReportsFailure()
    {
        using var temp = new TestDirectory();
        var attempts = 0;
        ControllableWatcher? initial = null;
        using var watcher = new DownloadWatcher(path =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
                return initial = new ControllableWatcher(path);
            throw new IOException("simulated restart failure");
        });
        var failed = new TaskCompletionSource<WatchFolderStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FolderStatusesChanged += statuses =>
        {
            if (statuses.SingleOrDefault() is { IsWatching: false } status)
                failed.TrySetResult(status);
        };
        watcher.Start([temp.Path]);

        initial!.RaiseError(new InternalBufferOverflowException());
        var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.False(status.IsWatching);
        Assert.Equal("Windows could not watch this folder.", status.Problem);
    }

    [Fact]
    public async Task WatcherError_SuccessfulRestartReportsRecovery()
    {
        using var temp = new TestDirectory();
        var attempts = 0;
        ControllableWatcher? initial = null;
        using var watcher = new DownloadWatcher(path =>
        {
            Interlocked.Increment(ref attempts);
            var created = new ControllableWatcher(path);
            initial ??= created;
            return created;
        });
        var recovered = new TaskCompletionSource<WatchFolderStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FolderStatusesChanged += statuses =>
        {
            if (Volatile.Read(ref attempts) >= 2 &&
                statuses.SingleOrDefault() is { IsWatching: true, Problem: null } status)
                recovered.TrySetResult(status);
        };
        watcher.Start([temp.Path]);

        initial!.RaiseError(new InternalBufferOverflowException());
        var status = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.True(status.IsWatching);
        Assert.Null(status.Problem);
    }

    [Fact]
    public async Task Restart_IgnoresLateEventsFromThePreviousWatcher()
    {
        using var oldFolder = new TestDirectory();
        using var currentFolder = new TestDirectory();
        var createdWatchers = new List<ControllableWatcher>();
        using var watcher = new DownloadWatcher(path =>
        {
            var created = new ControllableWatcher(path);
            createdWatchers.Add(created);
            return created;
        });
        var readyFiles = new List<string>();
        var currentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileReady += path =>
        {
            lock (readyFiles)
                readyFiles.Add(path);
            if (path.StartsWith(currentFolder.Path, StringComparison.OrdinalIgnoreCase))
                currentReady.TrySetResult();
        };

        watcher.Start([oldFolder.Path]);
        var previousWatcher = Assert.Single(createdWatchers);
        watcher.Start([currentFolder.Path]);
        var currentWatcher = createdWatchers[1];
        var oldFile = oldFolder.File("old.pmp");
        var currentFile = currentFolder.File("current.pmp");
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(currentFile, "current");

        previousWatcher.RaiseCreated(oldFile);
        currentWatcher.RaiseCreated(currentFile);
        await currentReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(TimeSpan.FromSeconds(2));

        lock (readyFiles)
        {
            Assert.DoesNotContain(oldFile, readyFiles);
            Assert.Contains(currentFile, readyFiles);
        }
    }

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

        var generated = temp.File("generated_dt.ttmp2");
        watcher.Ignore(generated);
        await File.WriteAllTextAsync(generated, "generated");
        var package = temp.File("mod.pmp");
        await File.WriteAllTextAsync(package, "package");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // FileSystemWatcher commonly delivers several Created/Changed events for one write.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(1, Volatile.Read(ref count));
    }

    private sealed class ControllableWatcher(string path) : FileSystemWatcher(path)
    {
        public void RaiseError(Exception exception) => OnError(new ErrorEventArgs(exception));

        public void RaiseCreated(string fullPath) => OnCreated(new FileSystemEventArgs(
            WatcherChangeTypes.Created,
            System.IO.Path.GetDirectoryName(fullPath)!,
            System.IO.Path.GetFileName(fullPath)));
    }
}
