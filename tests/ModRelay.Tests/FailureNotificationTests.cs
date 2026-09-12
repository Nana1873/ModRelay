using System.Net;
using System.Text;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class FailureNotificationTests
{
    [Fact]
    public async Task RejectedImport_StaysSilentWhenErrorNotificationsAreDisabled()
    {
        using var temp = new TestDirectory();
        var package = temp.File("quiet-rejection.pmp");
        File.WriteAllText(package, "package");
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => Json("{}"),
            post: () => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            showErrorNotifications: false);

        await pipeline.ProcessAsync(package, CancellationToken.None);

        Assert.Empty(ui.Notifications);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task RejectedImport_ShowsErrorAndKeepsSource()
    {
        using var temp = new TestDirectory();
        var package = temp.File("rejected.pmp");
        File.WriteAllText(package, "package");
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => Json("{}"),
            post: () => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await pipeline.ProcessAsync(package, CancellationToken.None);

        var notification = Assert.Single(ui.Notifications);
        Assert.Equal("Import failed", notification.Title);
        Assert.Contains("rejected", notification.Message);
        Assert.Contains("500", notification.Message);
        Assert.True(notification.IsError);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task UnreachablePenumbra_ShowsNotificationAndQueuesPackage()
    {
        using var temp = new TestDirectory();
        var package = temp.File("offline.pmp");
        File.WriteAllText(package, "package");
        var ui = new RecordingInteraction();
        var pending = new PendingQueue(temp.File("pending.json"));
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => throw new HttpRequestException("offline"),
            post: () => throw new InvalidOperationException()), pending);

        await pipeline.ProcessAsync(package, CancellationToken.None);

        var notification = Assert.Single(ui.Notifications);
        Assert.Equal("Penumbra is unavailable", notification.Title);
        Assert.Equal(1, pending.Count);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task MultipleOfflineDownloads_ShowOneNoticeAndKeepEveryPackageQueued()
    {
        using var temp = new TestDirectory();
        var first = temp.File("first.pmp");
        var second = temp.File("second.pmp");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var ui = new RecordingInteraction();
        var pending = new PendingQueue(temp.File("pending.json"));
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => throw new HttpRequestException("offline"),
            post: () => throw new InvalidOperationException()), pending);

        await pipeline.ProcessAsync(first, CancellationToken.None);
        await pipeline.ProcessAsync(second, CancellationToken.None);

        Assert.Equal("Penumbra is unavailable", Assert.Single(ui.Notifications).Title);
        Assert.Equal(2, pending.Count);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task DamagedArchive_ShowsErrorAndKeepsArchive()
    {
        using var temp = new TestDirectory();
        var archive = temp.File("damaged.zip");
        File.WriteAllText(archive, "not an archive");
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => Json("{}"),
            post: () => Json("{}")));

        await pipeline.ProcessAsync(archive, CancellationToken.None);

        var notification = Assert.Single(ui.Notifications);
        Assert.Equal("Archive could not be read", notification.Title);
        Assert.True(notification.IsError);
        Assert.True(File.Exists(archive));
        Assert.Equal(0, ui.ProgressStarts);
        Assert.Equal(0, ui.ProgressEnds);
    }

    [Fact]
    public async Task AcceptedImport_StaysSilentAndKeepsSource()
    {
        using var temp = new TestDirectory();
        var package = temp.File("slow.pmp");
        File.WriteAllText(package, "package");
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => Json("{}"),
            post: () => Json("{}")), timeoutSeconds: 1);

        await pipeline.ProcessAsync(package, CancellationToken.None);

        Assert.Empty(ui.Notifications);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task UnsavedJob_DoesNotReachPenumbraAndReportsPersistenceFailure()
    {
        using var temp = new TestDirectory();
        var package = temp.File("queued.pmp");
        File.WriteAllText(package, "package");
        var queuePath = temp.File("pending.json");
        Directory.CreateDirectory(queuePath);
        var pending = new PendingQueue(queuePath);
        Assert.False(pending.Add(package));
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => Json("{}"),
            post: () => Json("{}")), pending);

        await pipeline.ProcessAsync(package, CancellationToken.None);

        Assert.Contains(ui.Notifications, notification =>
            notification.Title == "Queue could not be saved" && notification.IsError);
        Assert.Equal(1, pending.Count);
    }

    private static ModPipeline CreatePipeline(
        TestDirectory temp,
        IUserInteraction ui,
        HttpMessageHandler handler,
        PendingQueue? pending = null,
        int timeoutSeconds = 60,
        bool showErrorNotifications = true)
    {
        var config = new AppConfig
        {
            WatchFolders = [temp.Path],
            ShowErrorNotifications = showErrorNotifications,
            AutoUpgradeToDawntrail = false,
            PenumbraTimeoutSeconds = timeoutSeconds
        };
        return new ModPipeline(
            () => config,
            new ArchiveExtractor(),
            new TexToolsUpgrader(),
            new PenumbraClient(new HttpClient(handler), () => config),
            pending ?? new PendingQueue(temp.File("pending.json")),
            ui);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(
        Func<HttpResponseMessage> get,
        Func<HttpResponseMessage> post) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(request.Method == HttpMethod.Get ? get() : post());
    }

    private sealed class RecordingInteraction : IUserInteraction
    {
        public int ProgressStarts { get; private set; }
        public int ProgressEnds { get; private set; }

        public Task BeginArchiveProgressAsync(string archiveName, string message)
        {
            ProgressStarts++;
            return Task.CompletedTask;
        }
        public void UpdateArchiveProgress(string message) { }
        public Task EndArchiveProgressAsync()
        {
            ProgressEnds++;
            return Task.CompletedTask;
        }

        public List<(string Title, string Message, bool IsError)> Notifications { get; } = [];

        public Task<IReadOnlyList<string>> SelectArchiveEntriesAsync(
            string archivePath,
            IReadOnlyList<ArchiveEntryInfo> entries, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Notify(string title, string message, bool isError = false) =>
            Notifications.Add((title, message, isError));

        public void Status(string message)
        {
        }
    }
}
