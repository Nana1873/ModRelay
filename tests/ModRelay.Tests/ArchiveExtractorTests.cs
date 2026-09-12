using System.IO.Compression;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class ArchiveExtractorTests
{
    [Fact]
    public void Inspect_ReturnsPreDtAndCurrentModsWithoutFiltering()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("mods.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "folder/Fancy Outfit (Pre-DT).ttmp2", "old");
            WriteEntry(archive, "Fancy Outfit DT.ttmp2", "new");
            WriteEntry(archive, "readme.txt", "ignore");
        }

        var entries = new ArchiveExtractor().Inspect(archivePath);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.FileName.Contains("Pre-DT") && entry.LooksPreDawntrail);
        Assert.Contains(entries, entry => entry.FileName.Contains(" DT") && !entry.LooksPreDawntrail);
    }

    [Fact]
    public void Extract_FlattensPathsAndAvoidsOverwritingSameNames()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("mods.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "a/mod.ttmp2", "one");
            WriteEntry(archive, "b/mod.ttmp2", "two");
        }

        var extractor = new ArchiveExtractor();
        var entries = extractor.Inspect(archivePath);
        var output = System.IO.Path.Combine(temp.Path, "out");
        var files = extractor.Extract(archivePath, entries.Select(entry => entry.Key), output);

        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, file => Assert.Equal(output, System.IO.Path.GetDirectoryName(file)));
    }

    [Fact]
    public void Extract_SelectionDistinguishesArchivePathsThatDifferOnlyByCase()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("variants.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mod.pmp", "selected");
            WriteEntry(archive, "MOD.pmp", "not selected");
        }

        var files = new ArchiveExtractor().Extract(archivePath, ["mod.pmp"], temp.File("out"));

        Assert.Equal("selected", File.ReadAllText(Assert.Single(files)));
    }

    [Fact]
    public void Extract_DoesNotDeleteAFileCreatedBySomeoneElseBeforeOutputIsOpened()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("race.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            WriteEntry(archive, "mod.pmp", "archive content");
        var output = temp.File("out");
        var collision = System.IO.Path.Combine(output, "mod.pmp");
        var progress = new InlineProgress(_ => File.WriteAllText(collision, "someone else's file"));

        Assert.Throws<IOException>(() =>
            new ArchiveExtractor().Extract(archivePath, ["mod.pmp"], output, progress));

        Assert.Equal("someone else's file", File.ReadAllText(collision));
    }

    [Fact]
    public void Extract_CancellationDuringAnEntryRemovesOnlyItsOwnPartialOutput()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("cancel.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            WriteEntry(archive, "mod.pmp", new string('x', 1024));
        var output = temp.File("out");
        Directory.CreateDirectory(output);
        var existing = System.IO.Path.Combine(output, "existing.pmp");
        File.WriteAllText(existing, "keep");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());

        Assert.Throws<OperationCanceledException>(() =>
            new ArchiveExtractor().Extract(archivePath, ["mod.pmp"], output, progress, cancellation.Token));

        Assert.Equal(existing, Assert.Single(Directory.EnumerateFiles(output)));
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public void Inspect_RejectsWindowsDeviceAndAlternateStreamNames()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "safe.pmp", "safe");
            WriteEntry(archive, "CON.pmp", "device");
            WriteEntry(archive, "mod:stream.pmp", "stream");
        }

        var entries = new ArchiveExtractor().Inspect(archivePath);

        Assert.Equal("safe.pmp", Assert.Single(entries).FileName);
    }

    [Fact]
    public void Extract_StopsWhenConfiguredByteLimitIsExceededAndRemovesPartialOutput()
    {
        using var temp = new TestDirectory();
        var archivePath = temp.File("large.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            WriteEntry(archive, "large.pmp", new string('x', 1024));

        var extractor = new ArchiveExtractor(maxExtractedBytes: 128);
        var entry = Assert.Single(extractor.Inspect(archivePath));
        var output = System.IO.Path.Combine(temp.Path, "out");

        Assert.Throws<InvalidDataException>(() => extractor.Extract(archivePath, [entry.Key], output));
        Assert.Empty(Directory.EnumerateFiles(output));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
