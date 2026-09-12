using System.Collections.Concurrent;

namespace ModRelay.Core;

/// <summary>
/// Watches the configured folders and reports mod files once they are fully downloaded.
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string, FileSystemWatcher> _watcherFactory;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _folderOrder = [];
    private readonly Dictionary<string, WatchFolderStatus> _folderStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileFingerprint> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _watchStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private Timer? _poller;
    private IReadOnlyList<WatchFolderStatus> _folderStatuses = Array.Empty<WatchFolderStatus>();
    private long _generation;
    private int _paused;

    public DownloadWatcher() : this(folder => new FileSystemWatcher(folder))
    {
    }

    internal DownloadWatcher(Func<string, FileSystemWatcher> watcherFactory)
    {
        _watcherFactory = watcherFactory;
    }

    /// <summary>Raised once per file, when it is complete and readable.</summary>
    public event Action<string>? FileReady;

    /// <summary>Raised with a complete snapshot after folder readiness changes.</summary>
    public event Action<IReadOnlyList<WatchFolderStatus>>? FolderStatusesChanged;

    /// <summary>Immutable snapshot of every configured folder and its current readiness.</summary>
    public IReadOnlyList<WatchFolderStatus> FolderStatuses => Volatile.Read(ref _folderStatuses);

    /// <summary>While paused, new files are still noticed but not reported.</summary>
    public bool Paused
    {
        get => Volatile.Read(ref _paused) != 0;
        set => Volatile.Write(ref _paused, value ? 1 : 0);
    }

    /// <summary>
    /// Ignores our generated paths for this session, including conversions that take hours.
    /// Manual imports remain available; watching never scans existing files after a restart.
    /// </summary>
    public void Ignore(string path)
    {
        _ignored[path] = 0;
        _pending.TryRemove(path, out _);
    }

    public void Start(IEnumerable<string> folders)
    {
        IReadOnlyList<WatchFolderStatus> snapshot;
        long generation;
        lock (_gate)
        {
            generation = Interlocked.Increment(ref _generation);
            StopResourcesLocked();

            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _folderOrder.Add(folder);
                if (TryCreateWatcher(folder, generation, out var watcher, out var problem))
                {
                    _watchStarted[folder] = DateTime.UtcNow;
                    _watchers.Add(watcher);
                    _folderStates[folder] = new WatchFolderStatus(folder, true, null);
                    Log.Info($"Watching {folder}");
                }
                else
                {
                    _folderStates[folder] = new WatchFolderStatus(folder, false, problem);
                }
            }

            if (_watchers.Count > 0)
                _poller = new Timer(_ => PollPendingSafely(generation), null, PollInterval, PollInterval);

            snapshot = CaptureStatusesLocked();
        }

        PublishStatuses(snapshot, generation);
    }

    public void Stop()
    {
        IReadOnlyList<WatchFolderStatus> snapshot;
        long generation;
        bool changed;
        lock (_gate)
        {
            changed = _folderStatuses.Count > 0;
            generation = Interlocked.Increment(ref _generation);
            StopResourcesLocked();
            snapshot = CaptureStatusesLocked();
        }

        if (changed)
            PublishStatuses(snapshot, generation);
    }

    private void StopResourcesLocked()
    {
        _poller?.Dispose();
        _poller = null;

        foreach (var watcher in _watchers)
            TryDisposeWatcher(watcher);

        _watchers.Clear();
        _folderOrder.Clear();
        _folderStates.Clear();
        _pending.Clear();
        _watchStarted.Clear();
    }

    private bool TryCreateWatcher(
        string folder,
        long generation,
        out FileSystemWatcher watcher,
        out string? problem)
    {
        watcher = null!;
        problem = null;
        if (!Directory.Exists(folder))
        {
            problem = "Folder does not exist or is unavailable.";
            Log.Warn($"Watch folder does not exist or is unavailable: {folder}");
            return false;
        }

        FileSystemWatcher? candidate = null;
        try
        {
            candidate = _watcherFactory(folder) ??
                throw new InvalidOperationException("The watcher factory did not create a watcher.");
            candidate.IncludeSubdirectories = false;
            candidate.NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite;
            candidate.InternalBufferSize = 64 * 1024;
            var ownedWatcher = candidate;
            candidate.Created += (_, e) => OnChanged(ownedWatcher, e, generation);
            candidate.Changed += (_, e) => OnChanged(ownedWatcher, e, generation);
            candidate.Renamed += (_, e) => OnRenamed(ownedWatcher, e, generation);
            candidate.Error += (_, e) =>
            {
                var exception = e.GetException();
                ThreadPool.QueueUserWorkItem(
                    _ =>
                    {
                        try
                        {
                            RecoverAfterWatcherError(folder, ownedWatcher, generation, exception);
                        }
                        catch (Exception recoveryError)
                        {
                            Log.Error($"Unexpected watcher recovery failure for {folder}.", recoveryError);
                        }
                    });
            };
            candidate.EnableRaisingEvents = true;
            watcher = candidate;
            return true;
        }
        catch (Exception ex)
        {
            if (candidate is not null)
                TryDisposeWatcher(candidate);
            problem = ex is UnauthorizedAccessException
                ? "Windows denied access to this folder."
                : "Windows could not watch this folder.";
            Log.Error($"Could not watch {folder}.", ex);
            return false;
        }
    }

    private void OnChanged(FileSystemWatcher watcher, FileSystemEventArgs e, long generation)
    {
        lock (_gate)
        {
            if (_generation == generation &&
                _watchers.Contains(watcher) &&
                !IsIgnored(e.FullPath) &&
                IsInteresting(e.FullPath))
                _pending.TryAdd(e.FullPath, -1);
        }
    }

    private void OnRenamed(FileSystemWatcher watcher, RenamedEventArgs e, long generation)
    {
        lock (_gate)
        {
            if (_generation != generation || !_watchers.Contains(watcher))
                return;

            // Browsers finish a download by renaming "foo.zip.crdownload" to "foo.zip".
            _pending.TryRemove(e.OldFullPath, out _);
            if (!IsIgnored(e.FullPath) && IsInteresting(e.FullPath))
                _pending.TryAdd(e.FullPath, -1);
        }
    }

    private static bool IsInteresting(string path) =>
        ModFileTypes.IsModFile(path) || ModFileTypes.IsArchive(path);

    private void PollPendingSafely(long generation)
    {
        try
        {
            PollPending(generation);
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected error while checking pending downloads.", ex);
        }
    }

    private void PollPending(long generation)
    {
        if (Volatile.Read(ref _generation) != generation || Paused)
            return;

        foreach (var (path, previousSize) in _pending.ToArray())
        {
            if (IsIgnored(path))
            {
                lock (_gate)
                {
                    if (_generation != generation)
                        return;
                    _pending.TryRemove(path, out _);
                }
                continue;
            }

            if (!File.Exists(path))
            {
                lock (_gate)
                {
                    if (_generation != generation)
                        return;
                    _pending.TryRemove(path, out _);
                }
                continue;
            }

            if (!FileReadiness.IsReady(path, previousSize, out var currentSize))
            {
                lock (_gate)
                {
                    if (_generation != generation)
                        return;
                    _pending[path] = currentSize;
                }
                continue;
            }

            lock (_gate)
            {
                if (_generation != generation)
                    return;
                if (!_pending.TryRemove(path, out _))
                    continue;
            }

            if (!TryGetFingerprint(path, out var fingerprint))
                continue;

            if (_processed.TryGetValue(path, out var previous) && previous == fingerprint)
                continue;

            lock (_gate)
            {
                if (_generation != generation)
                    return;

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
    }

    private void RecoverAfterWatcherError(
        string folder,
        FileSystemWatcher failedWatcher,
        long watcherGeneration,
        Exception? exception)
    {
        IReadOnlyList<WatchFolderStatus> snapshot;
        long generation;
        DateTime? started;
        lock (_gate)
        {
            if (_generation != watcherGeneration || !_watchers.Remove(failedWatcher))
                return;

            generation = _generation;
            Log.Warn($"Watcher error in {folder}; attempting one restart.", exception);
            TryDisposeWatcher(failedWatcher);

            if (TryCreateWatcher(folder, generation, out var replacement, out var problem))
            {
                _watchers.Add(replacement);
                _folderStates[folder] = new WatchFolderStatus(
                    folder,
                    true,
                    "The watcher restarted after a file system error.");
                Log.Info($"Watching resumed for {folder}");
            }
            else
            {
                _folderStates[folder] = new WatchFolderStatus(folder, false, problem);
            }

            started = _watchStarted.TryGetValue(folder, out var value) ? value : null;
            snapshot = CaptureStatusesLocked();
        }

        PublishStatuses(snapshot, generation);

        if (started is not null && !TryRescanChangedFiles(folder, started.Value, generation))
            ReportIncompleteRecovery(folder, generation);
        else
            ClearRecoveryProblem(folder, generation);
    }

    private bool TryRescanChangedFiles(string folder, DateTime started, long generation)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (Volatile.Read(ref _generation) != generation)
                    return true;

                if (!IsInteresting(path) || IsIgnored(path))
                    continue;

                if (File.GetLastWriteTimeUtc(path) >= started)
                {
                    lock (_gate)
                    {
                        if (_generation != generation)
                            return true;
                        _pending.TryAdd(path, -1);
                    }
                }
            }

            lock (_gate)
            {
                if (_generation != generation)
                    return true;
                _watchStarted[folder] = DateTime.UtcNow;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check files changed during watcher recovery for {folder}.", ex);
            return false;
        }
    }

    private void ReportIncompleteRecovery(string folder, long generation)
    {
        IReadOnlyList<WatchFolderStatus>? snapshot = null;
        lock (_gate)
        {
            if (_generation != generation ||
                !_folderStates.TryGetValue(folder, out var current) ||
                !current.IsWatching)
                return;

            _folderStates[folder] = current with
            {
                Problem = "Watching resumed, but files changed during recovery could not be checked."
            };
            snapshot = CaptureStatusesLocked();
        }

        PublishStatuses(snapshot, generation);
    }

    private void ClearRecoveryProblem(string folder, long generation)
    {
        IReadOnlyList<WatchFolderStatus>? snapshot = null;
        lock (_gate)
        {
            if (_generation != generation ||
                !_folderStates.TryGetValue(folder, out var current) ||
                !current.IsWatching ||
                current.Problem is null)
                return;

            _folderStates[folder] = current with { Problem = null };
            snapshot = CaptureStatusesLocked();
        }

        PublishStatuses(snapshot, generation);
    }

    private static void TryDisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not dispose a download-folder watcher cleanly.", ex);
        }
    }

    private IReadOnlyList<WatchFolderStatus> CaptureStatusesLocked()
    {
        IReadOnlyList<WatchFolderStatus> snapshot = Array.AsReadOnly(
            _folderOrder.Select(folder => _folderStates[folder]).ToArray());
        Volatile.Write(ref _folderStatuses, snapshot);
        return snapshot;
    }

    private void PublishStatuses(IReadOnlyList<WatchFolderStatus> snapshot, long generation)
    {
        if (Volatile.Read(ref _generation) != generation)
            return;

        var handlers = FolderStatusesChanged;
        if (handlers is null)
            return;

        foreach (Action<IReadOnlyList<WatchFolderStatus>> handler in handlers.GetInvocationList())
        {
            if (Volatile.Read(ref _generation) != generation)
                return;

            try
            {
                handler(snapshot);
            }
            catch (Exception ex)
            {
                Log.Warn("A watch-folder status handler failed.", ex);
            }
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

    private bool IsIgnored(string path) => _ignored.ContainsKey(path);

    public void Dispose() => Stop();

    private readonly record struct FileFingerprint(long Length, DateTime LastWriteUtc);
}
