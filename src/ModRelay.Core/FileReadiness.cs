namespace ModRelay.Core;

/// <summary>
/// Decides whether a file the watcher saw is actually finished downloading.
/// </summary>
public static class FileReadiness
{
    /// <summary>Partial-download markers used by the common browsers.</summary>
    private static readonly string[] PartialSuffixes = [".part", ".crdownload", ".download", ".tmp", ".!ut"];

    /// <summary>
    /// True when the file exists, no partial-download sibling is left over,
    /// the size has not changed since <paramref name="previousSize"/>, and it can be opened exclusively.
    /// </summary>
    public static bool IsReady(string path, long previousSize, out long currentSize)
    {
        currentSize = -1;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            currentSize = info.Length;

            if (currentSize == 0 || currentSize != previousSize)
                return false;

            if (HasPartialSibling(path))
                return false;

            // If anything still holds a write handle, this throws and we try again later.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool HasPartialSibling(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return false;

        var stem = Path.GetFileNameWithoutExtension(path);

        try
        {
            foreach (var suffix in PartialSuffixes)
            {
                // Firefox writes "foo.zip.part", Chrome "foo.zip.crdownload", others insert an id.
                if (Directory.EnumerateFiles(directory, $"{stem}*{suffix}").Any())
                    return true;
            }
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        return false;
    }
}
