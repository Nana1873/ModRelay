using SharpCompress.Archives;

namespace ModRelay.Core;

public sealed record ArchiveEntryInfo(string Key, string FileName, long Size, bool LooksPreDawntrail);

/// <summary>
/// Reads .zip/.7z/.rar and pulls the mod files out of them.
/// </summary>
public sealed class ArchiveExtractor
{
    private const long DefaultMaximumExtractedBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumModEntries = 500;
    private const int CopyBufferSize = 128 * 1024;
    private const long FreeSpaceReserve = 512L * 1024 * 1024;
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly long _maxExtractedBytes;

    public ArchiveExtractor(long maxExtractedBytes = DefaultMaximumExtractedBytes)
    {
        if (maxExtractedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));

        _maxExtractedBytes = maxExtractedBytes;
    }

    /// <summary>
    /// Every mod file in the archive. Pre-Dawntrail files are flagged, never dropped -
    /// they are the ones that need the TexTools upgrade.
    /// </summary>
    public IReadOnlyList<ArchiveEntryInfo> Inspect(string archivePath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);

        var entries = new List<ArchiveEntryInfo>();
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.Key is null || !ModFileTypes.IsModFile(entry.Key))
                continue;

            var fileName = Path.GetFileName(entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!IsSafeWindowsFileName(fileName))
            {
                Log.Warn($"Ignoring archive entry with an unsafe Windows file name: {entry.Key}");
                continue;
            }

            entries.Add(new ArchiveEntryInfo(
                entry.Key,
                fileName,
                entry.Size,
                ModFileTypes.LooksPreDawntrail(entry.Key)));

            if (entries.Count > MaximumModEntries)
                throw new InvalidDataException($"The archive contains more than {MaximumModEntries} mod packages.");
        }

        return entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
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
        long extractedBytes = 0;

        Directory.CreateDirectory(destinationDirectory);
        var extractionLimit = EffectiveExtractionLimit(destinationDirectory);

        using var archive = ArchiveFactory.OpenArchive(archivePath);

        try
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsDirectory || entry.Key is null || !wanted.Contains(entry.Key))
                    continue;

                var fileName = Path.GetFileName(entry.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!IsSafeWindowsFileName(fileName))
                    throw new InvalidDataException($"The archive entry '{entry.Key}' has an unsafe Windows file name.");

                if (entry.Size < 0 || entry.Size > extractionLimit - extractedBytes)
                    throw ExtractionLimitExceeded(extractionLimit);

                var destination = UniquePath(Path.Combine(destinationDirectory, fileName));
                progress?.Report($"Extracting {fileName}");

                try
                {
                    using var input = entry.OpenEntryStream();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[CopyBufferSize];
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                            break;

                        if (read > extractionLimit - extractedBytes)
                            throw ExtractionLimitExceeded(extractionLimit);

                        output.Write(buffer, 0, read);
                        extractedBytes += read;
                    }

                    if (output.Length == 0)
                        throw new InvalidDataException($"The archive entry '{entry.Key}' is empty.");
                }
                catch
                {
                    TryDelete(destination);
                    throw;
                }

                Log.Info($"Extracted {entry.Key} -> {destination}");
                extracted.Add(destination);
            }
        }
        catch
        {
            foreach (var path in extracted)
                TryDelete(path);
            throw;
        }

        return extracted;
    }

    private long EffectiveExtractionLimit(string destinationDirectory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
            var available = string.IsNullOrEmpty(root) ? _maxExtractedBytes : new DriveInfo(root).AvailableFreeSpace;
            return Math.Min(_maxExtractedBytes, Math.Max(0, available - FreeSpaceReserve));
        }
        catch
        {
            return _maxExtractedBytes;
        }
    }

    private static InvalidDataException ExtractionLimitExceeded(long limit) =>
        new($"The selected archive contents exceed the {FormatBytes(limit)} extraction safety limit.");

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024L * 1024 * 1024)} GB"
            : $"{bytes / (1024L * 1024)} MB";

    private static bool IsSafeWindowsFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return !WindowsDeviceNames.Contains(Path.GetFileNameWithoutExtension(fileName));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* The caller will report the extraction failure. */ }
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
