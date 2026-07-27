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
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                    };

                    watcher.Created += OnChanged;
                    watcher.Changed += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, e) => Log.Warn($"Watcher error in {folder}.", e.GetException());
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

            Log.Info($"Detected {path}");

            try
            {
                FileReady?.Invoke(path);
            }
            catch (Exception ex)
            {
                Log.Error($"Handler for {path} threw.", ex);
            }
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
}
