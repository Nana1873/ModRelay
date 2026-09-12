using System.IO.Compression;
using System.Net;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class ArchivePipelineTests
{
    [Fact]
    public async Task MultipleNestedMods_ShowMultiSelectAndExtractOnlyCheckedEntries()
    {
        using var temp = new TestDirectory();
        var archivePath = CreateArchive(temp, "nested/a/first.pmp", "other/folder/second.ttmp2");
        var ui = new FakeInteraction(entries => [entries.Single(entry => entry.FileName == "second.ttmp2").Key]);
        using var pipeline = CreatePipeline(temp, ui, extractAll: false);

        await pipeline.ProcessAsync(archivePath, CancellationToken.None);

        var output = Path.Combine(temp.Path, "bundle");
        Assert.Equal(1, ui.SelectionCount);
        Assert.Equal(1, ui.ProgressStarts);
        Assert.Equal(1, ui.ProgressEnds);
        Assert.Contains(ui.ProgressMessages, message => message.Contains("1 of 1", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(output, "first.pmp")));
        Assert.True(File.Exists(Path.Combine(output, "second.ttmp2")));
        Assert.True(File.Exists(archivePath));
        using var retainedArchive = ZipFile.OpenRead(archivePath);
        Assert.Equal(2, retainedArchive.Entries.Count);
    }

    [Fact]
    public async Task ExtractAll_SkipsDialogAndExtractsEveryNestedMod()
    {
        using var temp = new TestDirectory();
        var archivePath = CreateArchive(temp, "nested/a/first.pmp", "other/folder/second.ttmp2");
        var ui = new FakeInteraction(_ => throw new InvalidOperationException("Selection should not be shown."));
        using var pipeline = CreatePipeline(temp, ui, extractAll: true);

        await pipeline.ProcessAsync(archivePath, CancellationToken.None);

        var output = Path.Combine(temp.Path, "bundle");
        Assert.Equal(0, ui.SelectionCount);
        Assert.True(File.Exists(Path.Combine(output, "first.pmp")));
        Assert.True(File.Exists(Path.Combine(output, "second.ttmp2")));
    }

    [Fact]
    public async Task ExtractedFiles_AreIgnoredBeforeTheNextPackageStartsExtracting()
    {
        using var temp = new TestDirectory();
        var archivePath = CreateArchive(temp, "first.pmp", "second.pmp");
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstOutput = Path.Combine(temp.Path, "bundle", "first.pmp");
        var observedSecondEntry = false;
        var ui = new FakeInteraction(_ => [])
        {
            OnProgress = message =>
            {
                if (!message.Contains("second.pmp", StringComparison.Ordinal))
                    return;
                observedSecondEntry = true;
                Assert.True(File.Exists(firstOutput));
                Assert.Contains(firstOutput, ignored);
            }
        };
        using var pipeline = CreatePipeline(temp, ui, extractAll: true, path => ignored.Add(path));

        await pipeline.ProcessAsync(archivePath, CancellationToken.None);

        Assert.True(observedSecondEntry);
        Assert.Equal(2, ignored.Count);
    }

    [Fact]
    public async Task SingleNestedMod_SkipsDialogEvenWhenExtractAllIsOff()
    {
        using var temp = new TestDirectory();
        var archivePath = CreateArchive(temp, "deep/folder/only.pcp");
        var ui = new FakeInteraction(_ => throw new InvalidOperationException("Selection should not be shown."));
        using var pipeline = CreatePipeline(temp, ui, extractAll: false);

        await pipeline.ProcessAsync(archivePath, CancellationToken.None);

        Assert.Equal(0, ui.SelectionCount);
        Assert.True(File.Exists(Path.Combine(temp.Path, "bundle", "only.pcp")));
    }

    [Fact]
    public async Task EmptySelection_SkipsArchiveWithoutExtractingAnything()
    {
        using var temp = new TestDirectory();
        var archivePath = CreateArchive(temp, "first.pmp", "second.pmp");
        var ui = new FakeInteraction(_ => []);
        using var pipeline = CreatePipeline(temp, ui, extractAll: false);

        await pipeline.ProcessAsync(archivePath, CancellationToken.None);

        Assert.Equal(1, ui.SelectionCount);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "bundle")));
        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task UnrelatedArchive_IsIgnoredWithoutShowingArchiveUi()
    {
        using var temp = new TestDirectory();
        var archivePath = CreateArchive(temp, "photos/holiday.jpg", "documents/readme.txt");
        var ui = new FakeInteraction(_ => throw new InvalidOperationException("Selection should not be shown."));
        using var pipeline = CreatePipeline(temp, ui, extractAll: false);

        await pipeline.ProcessAsync(archivePath, CancellationToken.None);

        Assert.Equal(0, ui.SelectionCount);
        Assert.Equal(0, ui.ProgressStarts);
        Assert.Equal(0, ui.ProgressEnds);
        Assert.True(File.Exists(archivePath));
    }

    private static ModPipeline CreatePipeline(
        TestDirectory temp, IUserInteraction ui, bool extractAll, Action<string>? ignoreGeneratedFile = null)
    {
        var config = new AppConfig
        {
            WatchFolders = [temp.Path],
            ExtractAllMods = extractAll,
            AutoUpgradeToDawntrail = false
        };
        return new ModPipeline(
            () => config,
            new ArchiveExtractor(),
            new TexToolsUpgrader(),
            new PenumbraClient(new HttpClient(new AcceptingHandler()), () => config),
            new PendingQueue(temp.File("pending.json")),
            ui, ignoreGeneratedFile);
    }

    private sealed class AcceptingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }

    private static string CreateArchive(TestDirectory temp, params string[] entries)
    {
        var path = temp.File("bundle.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(name);
        }
        return path;
    }

    private sealed class FakeInteraction(Func<IReadOnlyList<ArchiveEntryInfo>, IReadOnlyList<string>> select) : IUserInteraction
    {
        public int SelectionCount { get; private set; }
        public int ProgressStarts { get; private set; }
        public int ProgressEnds { get; private set; }
        public List<string> ProgressMessages { get; } = [];
        public Action<string>? OnProgress { get; init; }

        public Task BeginArchiveProgressAsync(string archiveName, string message)
        {
            ProgressStarts++;
            return Task.CompletedTask;
        }

        public void UpdateArchiveProgress(string message)
        {
            ProgressMessages.Add(message);
            OnProgress?.Invoke(message);
        }

        public Task EndArchiveProgressAsync()
        {
            ProgressEnds++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> SelectArchiveEntriesAsync(
            string archivePath,
            IReadOnlyList<ArchiveEntryInfo> entries, CancellationToken cancellationToken = default)
        {
            SelectionCount++;
            return Task.FromResult(select(entries));
        }

        public Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Notify(string title, string message, bool isError = false)
        {
        }

        public void Status(string message)
        {
        }
    }
}
