using System.Collections.Concurrent;
using System.Net;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class PipelineQueueTests
{
    [Fact]
    public async Task ManualSubmissionDuringPendingRetry_DoesNotImportTheSamePackageTwice()
    {
        using var temp = new TestDirectory();
        var package = temp.File("queued.pmp");
        var nextPackage = temp.File("next.pmp");
        File.WriteAllText(package, "queued");
        File.WriteAllText(nextPackage, "next");
        var pending = new PendingQueue(temp.File("pending.json"));
        Assert.True(pending.Add(package));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = new ConcurrentQueue<string>();
        using var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                posts.Enqueue(body);
                if (posts.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                if (body.Contains("next.pmp", StringComparison.Ordinal))
                    nextStarted.TrySetResult();
            }
            return Ok();
        });
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, pending, handler, ui);
        pipeline.Start();
        pipeline.RetryPending();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pipeline.Enqueue(package);
        pipeline.Enqueue(package.ToUpperInvariant());
        pipeline.Enqueue(nextPackage);
        releaseFirst.TrySetResult();
        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, posts.Count);
        Assert.Equal(0, pending.Count);
        Assert.True(File.Exists(package));
        Assert.Empty(ui.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrInterruptedPendingImport_StopsRetryingAndKeepsTheSource(bool interrupted)
    {
        using var temp = new TestDirectory();
        var package = temp.File("queued.pmp");
        File.WriteAllText(package, "package");
        var pending = new PendingQueue(temp.File("pending.json"));
        Assert.True(pending.Add(package));
        var postCount = 0;
        using var handler = new CallbackHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Ok());

            Interlocked.Increment(ref postCount);
            if (interrupted)
                throw new HttpRequestException("The reply was lost after sending the request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, pending, handler, ui);
        pipeline.Start();
        pipeline.RetryPending();
        await ui.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pipeline.RetryPending();

        Assert.Equal(1, Volatile.Read(ref postCount));
        Assert.Equal(0, pending.Count);
        var reloaded = new PendingQueue(temp.File("pending.json"));
        Assert.True(reloaded.Load());
        Assert.Equal(0, reloaded.Count);
        Assert.True(File.Exists(package));
        Assert.True(Assert.Single(ui.Notifications).IsError);
    }

    private static ModPipeline CreatePipeline(
        TestDirectory temp, PendingQueue pending, HttpMessageHandler handler, IUserInteraction ui)
    {
        var config = new AppConfig { AutoUpgradeToDawntrail = false };
        return new ModPipeline(() => config, new ArchiveExtractor(), new TexToolsUpgrader(),
            new PenumbraClient(new HttpClient(handler, disposeHandler: false), () => config), pending, ui);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class RecordingInteraction : IUserInteraction
    {
        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(string Title, string Message, bool IsError)> Notifications { get; } = [];

        public Task BeginArchiveProgressAsync(string archiveName, string message) => Task.CompletedTask;
        public void UpdateArchiveProgress(string message) { }
        public Task EndArchiveProgressAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<string>> SelectArchiveEntriesAsync(
            string archivePath, IReadOnlyList<ArchiveEntryInfo> entries, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void Notify(string title, string message, bool isError = false) => Notifications.Enqueue((title, message, isError));
        public void Status(string message)
        {
            if (message == "Ready")
                Idle.TrySetResult();
        }
    }
}
