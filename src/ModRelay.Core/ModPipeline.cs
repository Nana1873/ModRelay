using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace ModRelay.Core;

public sealed record PipelineState(int QueuedCount, int PendingCount, bool IsBusy, bool CanCancel, string? CurrentFile)
{
    public int ReviewCount { get; init; }
    public bool SavePending { get; init; }
}

/// <summary>Processes durable source jobs serially, retaining package-level handoff progress.</summary>
[SupportedOSPlatform("windows")]
public sealed class ModPipeline : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private readonly Func<AppConfig> _config;
    private readonly ArchiveExtractor _extractor;
    private readonly TexToolsUpgrader _upgrader;
    private readonly PenumbraClient _penumbra;
    private readonly PendingQueue _pending;
    private readonly IUserInteraction _ui;
    private readonly Action<string> _ignoreGeneratedFile;
    private readonly Channel<QueuedWork> _queue = Channel.CreateUnbounded<QueuedWork>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _currentGate = new();
    private Task? _worker;
    private Timer? _retryTimer;
    private int _retryQueued;
    private Guid? _currentId;
    private int _currentIndex = -1;
    private string? _currentFile;
    private CancellationTokenSource? _currentCancellation;
    private bool _userCancelled;
    private bool _canCancel;

    public ModPipeline(Func<AppConfig> configProvider, ArchiveExtractor extractor, TexToolsUpgrader upgrader,
        PenumbraClient penumbra, PendingQueue pending, IUserInteraction ui, Action<string>? ignoreGeneratedFile = null)
    {
        _config = configProvider; _extractor = extractor; _upgrader = upgrader;
        _penumbra = penumbra; _pending = pending; _ui = ui;
        _ignoreGeneratedFile = ignoreGeneratedFile ?? (_ => { });
    }

    public event Action<PipelineState>? StateChanged;

    public PipelineState State
    {
        get
        {
            Guid? currentId;
            int currentIndex;
            string? currentFile;
            bool canCancel;
            lock (_currentGate)
            {
                currentId = _currentId; currentIndex = _currentIndex;
                currentFile = _currentFile; canCancel = _canCancel && !_userCancelled;
            }
            var queued = 0;
            var offline = 0;
            var review = 0;
            foreach (var job in _pending.SnapshotJobs())
            {
                if (job.ReviewReason is not null) { review++; continue; }
                if (job.Packages is null)
                {
                    if (job.Id != currentId) queued++;
                    continue;
                }
                for (var index = 0; index < job.Packages.Count; index++)
                {
                    var stage = job.Packages[index].Stage;
                    if (stage == PackageStage.Review) { review++; continue; }
                    if (stage is PackageStage.Completed or PackageStage.Skipped ||
                        (job.Id == currentId && index == currentIndex)) continue;
                    queued++;
                    if (stage == PackageStage.Offline) offline++;
                }
            }
            return new PipelineState(queued, offline, currentId is not null, canCancel, currentFile)
            {
                ReviewCount = review,
                SavePending = _pending.NeedsPersistence
            };
        }
    }

    public void Start()
    {
        if (_worker is not null) return;
        if (!_pending.Load())
            NotifyError("Queue could not be read", "The damaged journal was backed up. Submit missing mods again.");
        var review = State.ReviewCount;
        if (review > 0)
            NotifyError("Some mods need your attention",
                $"{review} retained job(s) need checking before resubmission. An interrupted handoff may already be in Penumbra; an interrupted upgrade may be incomplete.");
        _worker = Task.Run(() => RunAsync(_shutdown.Token));
        foreach (var job in _pending.SnapshotJobs().Where(HasImmediateWork)) Schedule(job.Id, retryOffline: false);
        _retryTimer = new Timer(_ => RetryPending(), null, RetryInterval, RetryInterval);
        RetryPending();
        PublishState();
    }

    /// <summary>Writes the source job before it can begin processing.</summary>
    public void Enqueue(string path)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!_pending.AddIncoming(Path.GetFullPath(path), out var id))
        {
            NotifyPersistenceFailure();
            PublishState();
            return;
        }
        Schedule(id, retryOffline: true);
        PublishState();
    }

    /// <summary>Cancels only the active source or selected archive batch, before handoff.</summary>
    public void CancelCurrent()
    {
        lock (_currentGate)
        {
            if (!_canCancel || _currentCancellation is null || _userCancelled) return;
            _userCancelled = true;
            _currentCancellation.Cancel();
        }
        PublishState();
    }

    private void Schedule(Guid id, bool retryOffline)
    {
        if (_scheduled.TryAdd(id, 0) && !_queue.Writer.TryWrite(new QueuedWork(id, retryOffline)))
            _scheduled.TryRemove(id, out _);
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(shutdown))
            {
                if (work.Id == Guid.Empty)
                {
                    try { await ScheduleRetriesAsync(shutdown); }
                    finally { Interlocked.Exchange(ref _retryQueued, 0); }
                    continue;
                }
                try { await RunJobAsync(work.Id, work.RetryOffline, shutdown); }
                finally { _scheduled.TryRemove(work.Id, out _); }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task RunJobAsync(Guid id, bool retryOffline, CancellationToken shutdown)
    {
        var job = _pending.Get(id);
        if (job is null || job.ReviewReason is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        lock (_currentGate)
        {
            _currentId = id; _currentIndex = -1; _currentFile = Path.GetFileName(job.SourcePath);
            _currentCancellation = cancellation; _userCancelled = false; _canCancel = true;
        }
        PublishState();
        try
        {
            RequireSaved(_pending.Flush());
            await ProcessJobAsync(id, retryOffline, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            bool userCancelled;
            lock (_currentGate) userCancelled = _userCancelled;
            if (userCancelled && !_pending.Cancel(id)) NotifyCancellationPersistenceFailure();
            else if (!userCancelled)
            {
                // A gracefully stopped converter has removed its unfinished output.
                // Such work is safe to resume; a surviving output still needs review.
                if (!_pending.Update(id, item =>
                {
                    foreach (var package in item.Packages ?? [])
                        if (package.Stage == PackageStage.Upgrading && !File.Exists(package.UpgradeTarget))
                        {
                            package.Stage = PackageStage.Preparing;
                            package.UpgradeTarget = null;
                        }
                })) NotifyPersistenceFailure();
            }
            Log.Info(userCancelled ? $"Cancelled {job.SourcePath}; sources were kept." : $"Paused {job.SourcePath} for shutdown.");
        }
        catch (QueuePersistenceException) { NotifyPersistenceFailure(); }
        catch (Exception ex)
        {
            Log.Error($"Processing {job.SourcePath} failed.", ex);
            _pending.Update(id, item => item.ReviewReason = ex.Message);
            NotifyError("Processing failed", $"{Path.GetFileName(job.SourcePath)}\n{ex.Message}");
        }
        finally
        {
            lock (_currentGate)
            {
                _currentId = null; _currentIndex = -1; _currentFile = null;
                _currentCancellation = null; _canCancel = false;
            }
            _ui.Status("Ready");
            PublishState();
        }
    }

    // Direct execution is useful for bounded integration tests; it uses the same journal.
    internal async Task ProcessAsync(string path, CancellationToken cancellationToken)
    {
        if (!_pending.AddIncoming(Path.GetFullPath(path), out var id))
        {
            NotifyPersistenceFailure();
            return;
        }
        await RunJobAsync(id, retryOffline: true, cancellationToken);
    }

    private async Task ProcessJobAsync(Guid id, bool retryOffline, CancellationToken cancellationToken)
    {
        var job = _pending.Get(id);
        if (job is null) return;
        if (job.Packages is null)
            await ExpandArchiveAsync(job, cancellationToken);

        for (var index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            job = _pending.Get(id);
            if (job?.Packages is null || index >= job.Packages.Count) return;
            var package = job.Packages[index];
            if (package.Stage is PackageStage.Completed or PackageStage.Skipped or PackageStage.Review ||
                (package.Stage == PackageStage.Offline && !retryOffline)) continue;
            lock (_currentGate) { _currentIndex = index; _currentFile = Path.GetFileName(package.Path); }
            PublishState();
            if (package.Stage is PackageStage.Sending or PackageStage.Upgrading)
            {
                SavePackage(id, index, item =>
                {
                    item.Stage = PackageStage.Review;
                    item.ReviewReason = "An external operation was interrupted. Check the retained files and Penumbra before resubmitting.";
                });
                continue;
            }
            if (!File.Exists(package.Path))
            {
                SavePackage(id, index, item => { item.Stage = PackageStage.Review; item.ReviewReason = "The source file is unavailable."; });
                NotifyError("Source file is unavailable", package.Path);
                continue;
            }
            await PreparePackageAsync(id, index, package, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            package = _pending.Get(id)?.Packages?[index];
            if (package?.Stage is PackageStage.Ready or PackageStage.Offline)
                await InstallAsync(id, index, package.Path, cancellationToken);
        }
    }

    private async Task ExpandArchiveAsync(PendingJob job, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(job.SourcePath);
        _ui.Status($"Inspecting {name}");
        IReadOnlyList<ArchiveEntryInfo> entries;
        try { entries = _extractor.Inspect(job.SourcePath); }
        catch (Exception ex)
        {
            RequireSaved(_pending.Update(job.Id, item => item.ReviewReason = "The archive could not be read."));
            NotifyError("Archive could not be read", $"{name} is damaged, incomplete, or uses an unsupported archive format.");
            Log.Warn($"Could not inspect {job.SourcePath}.", ex);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var selected = job.SelectedEntries ?? (_config().ExtractAllMods || entries.Count <= 1
            ? entries.Select(entry => entry.Key).ToList()
            : [.. await _ui.SelectArchiveEntriesAsync(job.SourcePath, entries, cancellationToken).WaitAsync(cancellationToken)]);
        cancellationToken.ThrowIfCancellationRequested();
        if (selected.Count == 0)
        {
            RequireSaved(_pending.Cancel(job.Id));
            return;
        }
        RequireSaved(_pending.Update(job.Id, item => item.SelectedEntries = selected));
        _ui.Status($"Extracting {name}");
        await _ui.BeginArchiveProgressAsync(name, $"Preparing to extract {selected.Count} selected mod(s)…");
        IReadOnlyList<string> extracted;
        var currentEntry = 0;
        try
        {
            var destination = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, Path.GetFileNameWithoutExtension(job.SourcePath));
            extracted = _extractor.Extract(job.SourcePath, selected, destination,
                new InlineProgress<string>(message =>
                {
                    _ui.Status(message);
                    _ui.UpdateArchiveProgress($"{++currentEntry} of {selected.Count} — {message}");
                }), cancellationToken, onOutputCreated: _ignoreGeneratedFile);
            // No package may be sent until the entire expansion is durably recorded.
            RequireSaved(_pending.Update(job.Id, item =>
            {
                item.Packages = extracted.Select(path => new PendingPackage { Path = path, Stage = PackageStage.Preparing }).ToList();
                item.SelectedEntries = null;
            }));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            RequireSaved(_pending.Update(job.Id, item => item.ReviewReason = ex.Message));
            NotifyError("Archive extraction stopped", ex.Message);
        }
        finally { await _ui.EndArchiveProgressAsync(); }
    }

    private async Task PreparePackageAsync(Guid id, int index, PendingPackage package, CancellationToken cancellationToken)
    {
        if (package.Stage == PackageStage.Preparing)
        {
            var config = _config();
            if (!config.AutoUpgradeToDawntrail || !CanUpgradeWithTexTools(package.Path))
                SavePackage(id, index, item => item.Stage = PackageStage.Ready);
            else if (string.IsNullOrWhiteSpace(config.TexToolsConsolePath) || !File.Exists(config.TexToolsConsolePath))
            {
                if (!ModFileTypes.LooksPreDawntrail(Path.GetFileName(package.Path)))
                    SavePackage(id, index, item => item.Stage = PackageStage.Ready);
                else
                    SaveUpgradeFailure(id, index, new UpgradeResult(UpgradeStatus.ToolMissing, null, -1, string.Empty));
            }
            else
            {
                var target = UniqueGeneratedPath(Path.Combine(Path.GetDirectoryName(package.Path)!,
                    Path.GetFileNameWithoutExtension(package.Path) + "_dt.ttmp2"));
                _ignoreGeneratedFile(target);
                RequireSaved(_pending.PrepareExternalOperation(id, index,
                    item => { item.Stage = PackageStage.Upgrading; item.UpgradeTarget = target; }));
                _ui.Status($"Upgrading {Path.GetFileName(package.Path)} … (this may take a few minutes)");
                var result = await _upgrader.UpgradeAsync(config.TexToolsConsolePath, package.Path, target, cancellationToken);
                if (result.Status == UpgradeStatus.Upgraded)
                {
                    _ignoreGeneratedFile(result.OutputPath!);
                    SavePackage(id, index, item =>
                    {
                        item.Path = result.OutputPath!; item.Stage = PackageStage.Ready; item.UpgradeTarget = null;
                    });
                }
                else if (result.Status == UpgradeStatus.NotNeeded)
                    SavePackage(id, index, item => { item.Stage = PackageStage.Ready; item.UpgradeTarget = null; });
                else SaveUpgradeFailure(id, index, result);
            }
        }
        package = _pending.Get(id)?.Packages?[index]!;
        if (package?.Stage != PackageStage.AwaitingDecision) return;
        var install = await _ui.ConfirmInstallWithoutUpgradeAsync(Path.GetFileName(package.Path),
            package.UpgradeFailure!, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SavePackage(id, index, item => item.Stage = install ? PackageStage.Ready : PackageStage.Skipped);
    }

    private void SaveUpgradeFailure(Guid id, int index, UpgradeResult result) => SavePackage(id, index, item =>
    {
        item.Stage = PackageStage.AwaitingDecision; item.UpgradeTarget = null;
        item.UpgradeFailure = result with { Output = string.Empty, OutputPath = null };
    });

    private async Task InstallAsync(Guid id, int index, string path, CancellationToken cancellationToken)
    {
        _ui.Status($"Sending {Path.GetFileName(path)} to Penumbra");
        InstallResult result;
        try
        {
            result = await _penumbra.InstallAsync(path, cancellationToken, beforeSend: () =>
            {
                lock (_currentGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _canCancel = false;
                }
                // Persist before the side effect. A crash after this point becomes a review hold.
                RequireSaved(_pending.PrepareExternalOperation(id, index, item => item.Stage = PackageStage.Sending));
            });
        }
        finally { lock (_currentGate) _canCancel = true; PublishState(); }
        switch (result.Outcome)
        {
            case InstallOutcome.Accepted:
                SavePackage(id, index, item => item.Stage = PackageStage.Completed);
                break;
            case InstallOutcome.PenumbraUnreachable:
                var wasEmpty = _pending.Count == 0;
                SavePackage(id, index, item => item.Stage = PackageStage.Offline);
                if (wasEmpty && _config().ShowErrorNotifications)
                    _ui.Notify("Penumbra is unavailable", "Your mods will be sent automatically when Penumbra is available.");
                break;
            default:
                SavePackage(id, index, item => { item.Stage = PackageStage.Review; item.ReviewReason = result.Message; });
                NotifyError("Import failed", result.Message is null ? result.ModName : $"{result.ModName}\n{result.Message}");
                break;
        }
    }

    private void SavePackage(Guid id, int index, Action<PendingPackage> update) => RequireSaved(_pending.UpdatePackage(id, index, update));
    private void RequireSaved(bool saved) { PublishState(); if (!saved) throw new QueuePersistenceException(); }
    private void NotifyPersistenceFailure() => NotifyError("Queue could not be saved",
        "Processing is paused until ModRelay can save its data folder. Keep the app running or submit the retained files again later.");
    private void NotifyCancellationPersistenceFailure()
    {
        const string message = "The cancellation could not be saved. Keep ModRelay running until storage recovers; restarting before it is saved may resume this job.";
        Log.Warn(message);
        NotifyError("Cancellation could not be saved", message);
    }
    private void NotifyError(string title, string message)
    {
        if (_config().ShowErrorNotifications) _ui.Notify(title, message, isError: true);
    }

    internal void RetryPending()
    {
        if (_shutdown.IsCancellationRequested || Interlocked.CompareExchange(ref _retryQueued, 1, 0) != 0) return;
        if (!_queue.Writer.TryWrite(new QueuedWork(Guid.Empty, false))) Interlocked.Exchange(ref _retryQueued, 0);
    }

    private async Task ScheduleRetriesAsync(CancellationToken cancellationToken)
    {
        if (!_pending.Flush()) return;
        var jobs = _pending.SnapshotJobs();
        var retryOffline = jobs.Any(job => job.Packages?.Any(package => package.Stage == PackageStage.Offline) == true) &&
            await _penumbra.IsReachableAsync(cancellationToken);
        foreach (var job in jobs)
            if (HasImmediateWork(job) || (retryOffline && job.ReviewReason is null &&
                job.Packages?.Any(package => package.Stage == PackageStage.Offline) == true))
                Schedule(job.Id, retryOffline);
        PublishState();
    }

    private static bool HasImmediateWork(PendingJob job) => job.ReviewReason is null &&
        (job.Packages is null || job.Packages.Any(package => package.Stage is
            PackageStage.Preparing or PackageStage.Ready or PackageStage.AwaitingDecision));

    private void PublishState()
    {
        try { StateChanged?.Invoke(State); }
        catch (Exception ex) { Log.Warn("Could not update the displayed queue state.", ex); }
    }

    private static bool CanUpgradeWithTexTools(string path) => Path.GetExtension(path) is var extension &&
        (extension.Equals(".ttmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ttmp2", StringComparison.OrdinalIgnoreCase));

    private static string UniqueGeneratedPath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        _retryTimer?.Dispose();
        if (_worker is not null && Task.CurrentId != _worker.Id)
        {
            try { _worker.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException ex) when (ex.InnerExceptions.All(error => error is OperationCanceledException)) { }
        }
        if (_worker is null || _worker.IsCompleted) _shutdown.Dispose();
    }

    private sealed class QueuePersistenceException : Exception;
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private readonly record struct QueuedWork(Guid Id, bool RetryOffline);
}
