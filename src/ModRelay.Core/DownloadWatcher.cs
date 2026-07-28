using System.Collections.Concurrent;

namespace ModRelay.Core;

/// <summary>
/// Watches the configured folders and reports mod files once they are fully downloaded.
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileFingerprint> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _watchStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private Timer? _poller;

    /// <summary>Raised once per file, when it is complete and readable.</summary>
    public event Action<string>? FileReady;

    /// <summary>While paused, new files are still noticed but not reported.</summary>
    public bool Paused { get; set; }

    /// <summary>Ignores files created by our own extraction or conversion.</summary>
    public void Ignore(string path, TimeSpan? duration = null)
    {
        _ignored[path] = DateTimeOffset.UtcNow.Add(duration ?? TimeSpan.FromMinutes(10));
        _pending.TryRemove(path, out _);
    }

    public void Start(IEnumerable<string> folders)
    {
        Stop();

        lock (_gate)
        {
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder))
                {
                    Log.Warn($"Watch folder does not exist: {folder}");
                    continue;
                }

                try
                {
                    _watchStarted[folder] = DateTime.UtcNow;
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                        InternalBufferSize = 64 * 1024
                    };

                    watcher.Created += OnChanged;
                    watcher.Changed += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, e) => RecoverAfterWatcherError(folder, e.GetException());
                    watcher.EnableRaisingEvents = true;

                    _watchers.Add(watcher);
                    Log.Info($"Watching {folder}");

                }
                catch (Exception ex)
                {
                    Log.Error($"Could not watch {folder}.", ex);
                }
            }

            _poller = new Timer(_ => PollPending(), null, PollInterval, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _poller?.Dispose();
            _poller = null;

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            _pending.Clear();
            _watchStarted.Clear();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsIgnored(e.FullPath) && IsInteresting(e.FullPath))
            _pending.TryAdd(e.FullPath, -1);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Browsers finish a download by renaming "foo.zip.crdownload" to "foo.zip".
        _pending.TryRemove(e.OldFullPath, out _);
        if (!IsIgnored(e.FullPath) && IsInteresting(e.FullPath))
            _pending.TryAdd(e.FullPath, -1);
    }

    private static bool IsInteresting(string path) =>
        ModFileTypes.IsModFile(path) || ModFileTypes.IsArchive(path);

    private void PollPending()
    {
        if (Paused)
            return;

        foreach (var (path, previousSize) in _pending.ToArray())
        {
            if (IsIgnored(path))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            if (!File.Exists(path))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            if (!FileReadiness.IsReady(path, previousSize, out var currentSize))
            {
                _pending[path] = currentSize;
                continue;
            }

            if (!_pending.TryRemove(path, out _))
                continue;

            if (!TryGetFingerprint(path, out var fingerprint))
                continue;

            if (_processed.TryGetValue(path, out var previous) && previous == fingerprint)
                continue;

            _processed[path] = fingerprint;

            Log.Info($"Detected {path}");

            try
            {
                FileReady?.Invoke(path);
            }
            catch (Exception ex)
            {
                _processed.TryRemove(path, out _);
                Log.Error($"Handler for {path} threw.", ex);
            }
        }
    }

    private void RecoverAfterWatcherError(string folder, Exception? exception)
    {
        Log.Warn($"Watcher error in {folder}; rescanning files changed since watching began.", exception);
        if (!_watchStarted.TryGetValue(folder, out var started))
            return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!IsInteresting(path) || IsIgnored(path))
                    continue;

                if (File.GetLastWriteTimeUtc(path) >= started)
                    _pending.TryAdd(path, -1);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not recover the watcher for {folder}.", ex);
        }
    }

    private static bool TryGetFingerprint(string path, out FileFingerprint fingerprint)
    {
        try
        {
            var info = new FileInfo(path);
            fingerprint = new FileFingerprint(info.Length, info.LastWriteTimeUtc);
            return info.Exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fingerprint = default;
            return false;
        }
    }

    private bool IsIgnored(string path)
    {
        if (!_ignored.TryGetValue(path, out var expires))
            return false;

        if (expires > DateTimeOffset.UtcNow)
            return true;

        _ignored.TryRemove(path, out _);
        return false;
    }

    public void Dispose() => Stop();

    private readonly record struct FileFingerprint(long Length, DateTime LastWriteUtc);
}
