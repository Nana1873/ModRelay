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
}
