using SharpCompress.Archives;
using SharpCompress.Common;

namespace ModRelay.Core;

public sealed record ArchiveEntryInfo(string Key, string FileName, long Size, bool LooksPreDawntrail);

/// <summary>
/// Reads .zip/.7z/.rar and pulls the mod files out of them.
/// </summary>
public sealed class ArchiveExtractor
{
    /// <summary>
    /// Every mod file in the archive. Pre-Dawntrail files are flagged, never dropped -
    /// they are the ones that need the TexTools upgrade.
    /// </summary>
    public IReadOnlyList<ArchiveEntryInfo> Inspect(string archivePath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);

        return archive.Entries
            .Where(e => !e.IsDirectory && e.Key is not null && ModFileTypes.IsModFile(e.Key))
            .Select(e => new ArchiveEntryInfo(
                e.Key!,
                Path.GetFileName(e.Key!.Replace('/', Path.DirectorySeparatorChar)),
                e.Size,
                ModFileTypes.LooksPreDawntrail(e.Key!)))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Extracts the given entries into <paramref name="destinationDirectory"/>, flattened
    /// to their file names, and returns the paths written.
    /// </summary>
    public IReadOnlyList<string> Extract(
        string archivePath,
        IEnumerable<string> entryKeys,
        string destinationDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var wanted = new HashSet<string>(entryKeys, StringComparer.OrdinalIgnoreCase);
        var extracted = new List<string>();

        Directory.CreateDirectory(destinationDirectory);

        using var archive = ArchiveFactory.OpenArchive(archivePath);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory || entry.Key is null || !wanted.Contains(entry.Key))
                continue;

            var fileName = Path.GetFileName(entry.Key.Replace('/', Path.DirectorySeparatorChar));
            var destination = UniquePath(Path.Combine(destinationDirectory, fileName));

            progress?.Report($"Extracting {fileName}");
            entry.WriteToFile(destination, new ExtractionOptions { Overwrite = true });

            Log.Info($"Extracted {entry.Key} -> {destination}");
            extracted.Add(destination);
        }

        return extracted;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
