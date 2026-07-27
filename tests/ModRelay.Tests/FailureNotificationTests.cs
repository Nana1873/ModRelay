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
    public async Task RejectedImport_ShowsErrorEvenWhenSuccessNotificationsAreDisabled()
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
        Assert.Equal(1, ui.ProgressStarts);
        Assert.Equal(1, ui.ProgressEnds);
    }

    [Fact]
    public async Task AcceptedButUnconfirmedImport_ShowsWarningAndKeepsSource()
    {
        using var temp = new TestDirectory();
        var package = temp.File("slow.pmp");
        File.WriteAllText(package, "package");
        var ui = new RecordingInteraction();
        using var pipeline = CreatePipeline(temp, ui, new ResponseHandler(
            get: () => Json("{}"),
            post: () => Json("{}")), timeoutSeconds: 1);

        await pipeline.ProcessAsync(package, CancellationToken.None);

        var notification = Assert.Single(ui.Notifications);
        Assert.Equal("Import accepted", notification.Title);
        Assert.False(notification.IsError);
        Assert.Contains("completion was not confirmed", notification.Message);
        Assert.True(File.Exists(package));
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
            ShowNotifications = false,
            ShowErrorNotifications = showErrorNotifications,
            AutoDeleteMods = true,
            AutoForwardToPenumbra = true,
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
            IReadOnlyList<ArchiveEntryInfo> entries) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result) =>
            Task.FromResult(false);

        public void Notify(string title, string message, bool isError = false) =>
            Notifications.Add((title, message, isError));

        public void Status(string message)
        {
        }
    }
}
