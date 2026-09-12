using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class DurablePipelineTests
{
    [Fact]
    public async Task StateCounts_TrackIncomingJobsActiveArchiveChildrenOfflineSubsetAndCompletionEvents()
    {
        using var temp = new TestDirectory();
        var archive = WriteArchive(temp);
        var next = WritePackage(temp, "next.pmp");
        var offline = WritePackage(temp, "offline.pmp");
        var queue = new PendingQueue(temp.File("pending.json"));
        Assert.True(queue.Add(offline));
        var selectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selection = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        release[1].SetResult(); // The second archive child does not need a separate observation gate.
        var postIndex = -1;
        var handler = new Handler
        {
            BeforePostAsync = async token =>
            {
                var index = Interlocked.Increment(ref postIndex);
                entered[index].TrySetResult();
                await release[index].Task.WaitAsync(token);
            }
        };
        var ui = new Interaction
        {
            Select = async token =>
            {
                selectionStarted.TrySetResult();
                return await selection.Task.WaitAsync(token);
            }
        };
        var events = new ConcurrentQueue<PipelineState>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pipeline = Create(queue, ui, handler,
            new AppConfig { ExtractAllMods = false, AutoUpgradeToDawntrail = false });
        pipeline.StateChanged += state =>
        {
            events.Enqueue(state);
            if (!state.IsBusy && state.QueuedCount == 0 && state.PendingCount == 0)
                completed.TrySetResult();
        };
        pipeline.Enqueue(archive);
        pipeline.Enqueue(next);

        Assert.Equal(3, pipeline.State.QueuedCount);
        Assert.Equal(1, pipeline.State.PendingCount);
        Assert.False(pipeline.State.IsBusy);

        pipeline.Start();
        await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, pipeline.State.QueuedCount); // The active, unexpanded archive is excluded.
        Assert.Equal(1, pipeline.State.PendingCount);
        Assert.Equal("bundle.zip", pipeline.State.CurrentFile);

        selection.SetResult(["first.pmp", "second.pmp"]);
        await entered[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, pipeline.State.QueuedCount); // Second child, independent package, offline package.
        Assert.Equal(1, pipeline.State.PendingCount);
        Assert.Equal("first.pmp", pipeline.State.CurrentFile);
        release[0].SetResult();

        await entered[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pipeline.State.QueuedCount); // Only the offline package remains behind the active source.
        Assert.Equal(1, pipeline.State.PendingCount);
        Assert.Equal("next.pmp", pipeline.State.CurrentFile);
        release[2].SetResult();

        await entered[3].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pipeline.State.IsBusy);
        Assert.Equal("offline.pmp", pipeline.State.CurrentFile);
        Assert.Equal(0, pipeline.State.QueuedCount);
        Assert.Equal(0, pipeline.State.PendingCount);
        release[3].SetResult();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pipeline.State.IsBusy);
        Assert.Equal(0, pipeline.State.QueuedCount);
        Assert.Equal(0, pipeline.State.PendingCount);
        Assert.Equal(4, handler.Posts.Count);
        Assert.Contains(events, state => !state.IsBusy && state.QueuedCount == 3 && state.PendingCount == 1);
        Assert.Contains(events, state => state.CurrentFile == "bundle.zip" && state.QueuedCount == 2 && state.PendingCount == 1);
        Assert.Contains(events, state => state.CurrentFile == "first.pmp" && state.QueuedCount == 3 && state.PendingCount == 1);
        Assert.Contains(events, state => state.CurrentFile == "next.pmp" && state.QueuedCount == 1 && state.PendingCount == 1);
        Assert.Contains(events, state => !state.IsBusy && state.QueuedCount == 0 && state.PendingCount == 0);
        Assert.All(events, state => Assert.InRange(state.PendingCount, 0, state.QueuedCount));
    }

    [Fact]
    public async Task IncomingJob_IsDurableBeforeWorkerStartsAndResumesAfterRestart()
    {
        using var temp = new TestDirectory();
        var package = WritePackage(temp, "incoming.pmp");
        var store = temp.File("pending.json");
        var firstQueue = new PendingQueue(store);
        using (var first = Create(firstQueue, new Interaction(), new Handler()))
            first.Enqueue(package);
        var restored = new PendingQueue(store);
        Assert.True(restored.Load());
        Assert.Equal(PackageStage.Preparing, Assert.Single(Assert.Single(restored.SnapshotJobs()).Packages!).Stage);
        var handler = new Handler();
        var ui = new Interaction();
        using var second = Create(restored, ui, handler);
        second.Start();
        await ui.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(handler.Posts);
        Assert.Empty(restored.SnapshotJobs());
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task PartlyAcceptedArchive_ResumesOnlyItsRemainingChildren()
    {
        using var temp = new TestDirectory();
        var archive = WriteArchive(temp);
        var store = temp.File("pending.json");
        var queue = new PendingQueue(store);
        var firstHandler = new Handler();
        using var shutdown = new CancellationTokenSource();
        using (var first = Create(queue, new Interaction(), firstHandler))
        {
            first.StateChanged += _ =>
            {
                var packages = queue.SnapshotJobs().SingleOrDefault()?.Packages;
                if (packages?.Count == 2 && packages[0].Stage == PackageStage.Completed)
                    shutdown.Cancel();
            };
            await first.ProcessAsync(archive, shutdown.Token);
        }
        Assert.Single(firstHandler.Posts);
        var saved = Assert.Single(new PendingQueue(store).SnapshotJobs());
        Assert.Equal(PackageStage.Completed, saved.Packages![0].Stage);
        Assert.Equal(PackageStage.Preparing, saved.Packages[1].Stage);
        var secondHandler = new Handler();
        var secondUi = new Interaction();
        using var second = Create(new PendingQueue(store), secondUi, secondHandler);
        second.Start();
        await secondUi.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("second.pmp", Assert.Single(secondHandler.Posts));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task ReadyConvertedOutput_ResumesWithoutAnotherUpgradeOrPrompt()
    {
        using var temp = new TestDirectory();
        var source = WritePackage(temp, "outfit-pre-dt.ttmp2");
        var converted = WritePackage(temp, "outfit-pre-dt_dt.ttmp2");
        var store = temp.File("pending.json");
        var queue = new PendingQueue(store);
        Assert.True(queue.AddIncoming(source, out var id));
        Assert.True(queue.UpdatePackage(id, 0, package => { package.Path = converted; package.Stage = PackageStage.Ready; }));
        var handler = new Handler();
        var ui = new Interaction();
        using var pipeline = Create(new PendingQueue(store), ui, handler,
            new AppConfig { AutoUpgradeToDawntrail = true, TexToolsConsolePath = temp.File("missing.exe") });
        pipeline.Start();
        await ui.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("outfit-pre-dt_dt.ttmp2", Assert.Single(handler.Posts));
        Assert.Equal(0, ui.Confirmations);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(converted));
    }

    [Theory]
    [InlineData((int)PackageStage.Sending)]
    [InlineData((int)PackageStage.Upgrading)]
    public async Task InterruptedExternalOperation_IsHeldForReviewInsteadOfRetried(int stageValue)
    {
        var stage = (PackageStage)stageValue;
        using var temp = new TestDirectory();
        var source = WritePackage(temp, "uncertain.pmp");
        var next = WritePackage(temp, "next.pmp");
        var store = temp.File("pending.json");
        var queue = new PendingQueue(store);
        Assert.True(queue.AddIncoming(source, out var id));
        Assert.True(queue.UpdatePackage(id, 0, package =>
        {
            package.Stage = stage;
            package.UpgradeTarget = stage == PackageStage.Upgrading ? temp.File("uncertain_dt.ttmp2") : null;
        }));
        var restored = new PendingQueue(store);
        Assert.True(restored.Load());
        Assert.Equal(PackageStage.Review, restored.Get(id)!.Packages![0].Stage);
        var handler = new Handler();
        var ui = new Interaction();
        using var pipeline = Create(restored, ui, handler);
        pipeline.Start();
        pipeline.Enqueue(next);
        await ui.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("next.pmp", Assert.Single(handler.Posts));
        Assert.Equal(1, pipeline.State.ReviewCount);
        Assert.Contains(ui.Notifications, notice => notice.Title == "Some mods need your attention");
    }

    [Fact]
    public async Task CancelCurrent_DiscardsOnlyTheSelectedArchiveJobAndContinuesNextSource()
    {
        using var temp = new TestDirectory();
        var archive = WriteArchive(temp);
        var next = WritePackage(temp, "next.pmp");
        var queue = new PendingQueue(temp.File("pending.json"));
        var selectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Interaction
        {
            Select = async token =>
            {
                selectionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }
        };
        var handler = new Handler();
        using var pipeline = Create(queue, ui, handler, new AppConfig { ExtractAllMods = false, AutoUpgradeToDawntrail = false });
        pipeline.Enqueue(archive);
        pipeline.Enqueue(next);
        pipeline.Start();
        await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pipeline.State.CanCancel);
        pipeline.CancelCurrent();
        await handler.FirstPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("next.pmp", Assert.Single(handler.Posts));
        Assert.DoesNotContain(queue.SnapshotJobs(), job => job.SourcePath == archive);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task CancelAfterFirstArchiveHandoff_KeepsThatAcceptanceAndContinuesNextIndependentJob()
    {
        using var temp = new TestDirectory();
        var archive = WriteArchive(temp);
        var next = WritePackage(temp, "next.pmp");
        var queue = new PendingQueue(temp.File("pending.json"));
        var nextPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler
        {
            AfterPost = body => { if (body.Contains("next.pmp", StringComparison.Ordinal)) nextPosted.TrySetResult(); }
        };
        using var pipeline = Create(queue, new Interaction(), handler);
        pipeline.StateChanged += _ =>
        {
            var children = queue.SnapshotJobs().FirstOrDefault(job => job.SourcePath == archive)?.Packages;
            if (children?.Count == 2 && children[0].Stage == PackageStage.Completed)
                pipeline.CancelCurrent();
        };
        pipeline.Enqueue(archive);
        pipeline.Enqueue(next);
        pipeline.Start();
        await nextPosted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handler.Posts.Count);
        Assert.DoesNotContain(handler.Posts, body => body.Contains("second.pmp", StringComparison.Ordinal));
        Assert.DoesNotContain(queue.SnapshotJobs(), job => job.SourcePath == archive);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task CancellationWithUnwritableJournal_RemainsStoppedUntilItsRemovalIsDurable()
    {
        using var temp = new TestDirectory();
        var archive = WriteArchive(temp);
        var next = WritePackage(temp, "next.pmp");
        var store = temp.File("pending.json");
        var queue = new PendingQueue(store);
        var selectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savePending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Interaction
        {
            Select = async token =>
            {
                selectionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }
        };
        var handler = new Handler();
        using var pipeline = Create(queue, ui, handler,
            new AppConfig { ExtractAllMods = false, AutoUpgradeToDawntrail = false });
        pipeline.StateChanged += state =>
        {
            if (state.SavePending && !state.IsBusy) savePending.TrySetResult();
            if (!state.SavePending && !state.IsBusy && queue.SnapshotJobs().Count == 0) recovered.TrySetResult();
        };
        pipeline.Enqueue(archive);
        pipeline.Enqueue(next);
        pipeline.Start();
        await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var readOnlyJournal = new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pipeline.CancelCurrent();
            await savePending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pipeline.State.SavePending);
            Assert.DoesNotContain(queue.SnapshotJobs(), job => job.SourcePath == archive);
            Assert.Contains(new PendingQueue(store).SnapshotJobs(), job => job.SourcePath == archive);
            Assert.Contains(queue.SnapshotJobs(), job => job.SourcePath == next);
            Assert.Empty(handler.Posts);
            var notice = Assert.Single(ui.Notifications, notice => notice.Title == "Cancellation could not be saved");
            Assert.Contains("restarting", notice.Message);
        }
        pipeline.RetryPending();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pipeline.State.SavePending);
        Assert.Contains("next.pmp", Assert.Single(handler.Posts));
        Assert.Empty(new PendingQueue(store).SnapshotJobs());
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void UnsavedIncomingState_IsVisibleEvenWhenErrorNotificationsAreDisabled()
    {
        using var temp = new TestDirectory();
        var source = WritePackage(temp, "unsaved.pmp");
        var store = temp.File("pending.json");
        Directory.CreateDirectory(store);
        var ui = new Interaction();
        using var pipeline = Create(new PendingQueue(store), ui, new Handler(),
            new AppConfig { ShowErrorNotifications = false, AutoUpgradeToDawntrail = false });
        pipeline.Enqueue(source);
        Assert.True(pipeline.State.SavePending);
        Assert.Empty(ui.Notifications);
    }

    [Fact]
    public async Task ShutdownDuringSelection_KeepsTheUnprocessedArchiveForRestart()
    {
        using var temp = new TestDirectory();
        var archive = WriteArchive(temp);
        var store = temp.File("pending.json");
        var selectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Interaction
        {
            Select = async token =>
            {
                selectionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }
        };
        using (var first = Create(new PendingQueue(store), ui, new Handler(),
            new AppConfig { ExtractAllMods = false, AutoUpgradeToDawntrail = false }))
        {
            first.Enqueue(archive);
            first.Start();
            await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var restored = new PendingQueue(store);
        Assert.Null(Assert.Single(restored.SnapshotJobs()).Packages);
        var nextUi = new Interaction();
        var handler = new Handler();
        using var second = Create(restored, nextUi, handler);
        second.Start();
        await nextUi.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handler.Posts.Count);
    }

    [Fact]
    public async Task HandoffWaitsForItsDurableMarker_AndRecoversAfterStorageBecomesWritable()
    {
        using var temp = new TestDirectory();
        var source = WritePackage(temp, "guarded.pmp");
        var store = temp.File("pending.json");
        var queue = new PendingQueue(store);
        var obstructOnce = true;
        ModPipeline? active = null;
        var handler = new Handler
        {
            BeforeGet = () =>
            {
                if (!obstructOnce) return;
                obstructOnce = false;
                File.Delete(store);
                Directory.CreateDirectory(store);
            },
            BeforePost = () =>
            {
                Assert.False(active!.State.CanCancel);
                using var persisted = JsonDocument.Parse(File.ReadAllText(store));
                Assert.Equal("Sending", persisted.RootElement.GetProperty("Jobs")[0]
                    .GetProperty("Packages")[0].GetProperty("Stage").GetString());
            }
        };
        var ui = new Interaction();
        using var pipeline = Create(queue, ui, handler);
        active = pipeline;
        await pipeline.ProcessAsync(source, CancellationToken.None);
        Assert.Empty(handler.Posts);
        Assert.Equal(PackageStage.Ready, Assert.Single(queue.SnapshotJobs()).Packages![0].Stage);
        Assert.Contains(ui.Notifications, notice => notice.Title == "Queue could not be saved");
        Directory.Delete(store);
        pipeline.Start();
        await handler.FirstPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(handler.Posts);
    }

    [Fact]
    public void LegacyPendingArray_MigratesAsReadyToSendWithoutConversion()
    {
        using var temp = new TestDirectory();
        var package = WritePackage(temp, "legacy.ttmp2");
        var store = temp.File("pending.json");
        File.WriteAllText(store, JsonSerializer.Serialize(new[] { package }));
        var queue = new PendingQueue(store);
        Assert.True(queue.Load());
        Assert.Equal(PackageStage.Offline, Assert.Single(queue.SnapshotJobs()).Packages![0].Stage);
        using var journal = JsonDocument.Parse(File.ReadAllText(store));
        Assert.Equal(1, journal.RootElement.GetProperty("Version").GetInt32());
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Jobs\":[null]}")]
    [InlineData("{\"Version\":1,\"Jobs\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"SourcePath\":\"a.pmp\",\"Packages\":[null]}]}")]
    [InlineData("{\"Version\":1,\"Jobs\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"SourcePath\":\"a.pmp\",\"Packages\":[{\"Path\":\"a.pmp\",\"Stage\":999}]}]}")]
    [InlineData("{\"Version\":1,\"Jobs\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"SourcePath\":\"a.pmp\"},{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"SourcePath\":\"b.pmp\"}]}")]
    public void InvalidJournalEntries_AreBackedUpRatherThanSilentlyStranded(string json)
    {
        using var temp = new TestDirectory();
        var store = temp.File("pending.json");
        File.WriteAllText(store, json);
        var queue = new PendingQueue(store);
        Assert.False(queue.Load());
        Assert.Empty(queue.SnapshotJobs());
        Assert.True(File.Exists(store + ".broken"));
    }

    private static ModPipeline Create(PendingQueue queue, Interaction ui, Handler handler, AppConfig? config = null)
    {
        config ??= new AppConfig { ExtractAllMods = true, AutoUpgradeToDawntrail = false };
        return new ModPipeline(() => config, new ArchiveExtractor(), new TexToolsUpgrader(),
            new PenumbraClient(new HttpClient(handler), () => config), queue, ui);
    }

    private static string WritePackage(TestDirectory temp, string name)
    {
        var path = temp.File(name);
        File.WriteAllText(path, "package");
        return path;
    }

    private static string WriteArchive(TestDirectory temp)
    {
        var path = temp.File("bundle.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in new[] { "first.pmp", "second.pmp" })
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(name);
        }
        return path;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Posts { get; } = [];
        public TaskCompletionSource FirstPost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? BeforeGet { get; init; }
        public Action? BeforePost { get; init; }
        public Func<CancellationToken, Task>? BeforePostAsync { get; init; }
        public Action<string>? AfterPost { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (BeforePostAsync is not null)
                    await BeforePostAsync(token);
                BeforePost?.Invoke();
                var body = await request.Content!.ReadAsStringAsync(token);
                Posts.Enqueue(body);
                AfterPost?.Invoke(body);
                FirstPost.TrySetResult();
            }
            else BeforeGet?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class Interaction : IUserInteraction
    {
        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(string Title, string Message)> Notifications { get; } = [];
        public Func<CancellationToken, Task<IReadOnlyList<string>>>? Select { get; init; }
        public int Confirmations { get; private set; }
        public Task BeginArchiveProgressAsync(string archiveName, string message) => Task.CompletedTask;
        public void UpdateArchiveProgress(string message) { }
        public Task EndArchiveProgressAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<string>> SelectArchiveEntriesAsync(string archivePath,
            IReadOnlyList<ArchiveEntryInfo> entries, CancellationToken cancellationToken = default) =>
            Select?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<string>>(entries.Select(entry => entry.Key).ToList());
        public Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result, CancellationToken cancellationToken = default)
        {
            Confirmations++;
            return Task.FromResult(false);
        }
        public void Notify(string title, string message, bool isError = false) => Notifications.Enqueue((title, message));
        public void Status(string message) { if (message == "Ready") Idle.TrySetResult(); }
    }
}
