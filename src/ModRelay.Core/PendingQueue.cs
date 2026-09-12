using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModRelay.Core;

/// <summary>A durable journal for incoming jobs and their package-specific handoffs.</summary>
public sealed class PendingQueue(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private List<PendingJob> _jobs = [];
    private bool _loaded;
    private bool _loadHealthy = true;
    private bool _needsPersistence;

    public PendingQueue() : this(AppPaths.PendingQueueFile) { }
    public int Count => SnapshotJobs().Sum(job => job.Packages?.Count(package => package.Stage == PackageStage.Offline) ?? 0);

    public bool Load()
    {
        lock (_gate)
        {
            if (_loaded)
                return _loadHealthy;
            _loaded = true;
            try
            {
                if (!File.Exists(filePath))
                    return true;
                using var document = JsonDocument.Parse(File.ReadAllText(filePath));
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var paths = document.RootElement.Deserialize<List<string>>() ?? [];
                    _jobs = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(path => NewJob(Path.GetFullPath(path), PackageStage.Offline)).ToList();
                    Persist();
                }
                else
                {
                    var journal = document.RootElement.Deserialize<QueueJournal>(JsonOptions)
                        ?? throw new JsonException("The job journal is empty.");
                    if (journal.Version != 1 || journal.Jobs is null)
                        throw new JsonException("The job journal version is unsupported.");
                    _jobs = journal.Jobs;
                    if (_jobs.Any(job => job is null || job.Id == Guid.Empty || string.IsNullOrWhiteSpace(job.SourcePath) ||
                                         job.SelectedEntries?.Any(string.IsNullOrWhiteSpace) == true ||
                                         job.Packages?.Any(package => package is null || string.IsNullOrWhiteSpace(package.Path) ||
                                             !Enum.IsDefined(package.Stage)) == true) ||
                        _jobs.Select(job => job.Id).Distinct().Count() != _jobs.Count)
                        throw new JsonException("The job journal contains an invalid job.");
                }

                // A restart cannot establish the outcome of an interrupted external operation.
                var recovered = false;
                foreach (var package in _jobs.SelectMany(job => job.Packages ?? []))
                {
                    if (package.Stage is not (PackageStage.Sending or PackageStage.Upgrading))
                        continue;
                    package.ReviewReason = package.Stage == PackageStage.Sending
                        ? "ModRelay stopped during a handoff. Check this mod in Penumbra before submitting it again."
                        : $"ModRelay stopped during an upgrade. Check the retained source and output: {package.UpgradeTarget}";
                    package.Stage = PackageStage.Review;
                    recovered = true;
                }
                if (recovered)
                    Persist();
                foreach (var job in _jobs)
                {
                    if (job.ReviewReason is not null)
                        Log.Warn($"Job needs review: {job.SourcePath}. {job.ReviewReason}");
                    foreach (var package in (job.Packages ?? []).Where(package => package.Stage == PackageStage.Review))
                        Log.Warn($"Package needs review: {package.Path}. {package.ReviewReason}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the job journal at {filePath}.", ex);
                _jobs = [];
                _loadHealthy = false;
                try { File.Copy(filePath, filePath + ".broken", overwrite: true); }
                catch (Exception backupError) { Log.Warn("Could not back up the unreadable job journal.", backupError); }
                return false;
            }
        }
    }

    internal bool AddIncoming(string path, out Guid jobId)
    {
        lock (_gate)
        {
            Load();
            path = Path.GetFullPath(path);
            var existing = _jobs.FirstOrDefault(job => SamePath(job.SourcePath, path) ||
                job.Packages?.Any(package => SamePath(package.Path, path)) == true);
            if (existing is not null)
            {
                existing.ReviewReason = null;
                // Explicit resubmission retries holds, preserving accepted archive siblings.
                foreach (var package in existing.Packages ?? [])
                {
                    if (package.Stage != PackageStage.Review ||
                        (!SamePath(existing.SourcePath, path) && !SamePath(package.Path, path)))
                        continue;
                    package.Stage = package.UpgradeTarget is null ? PackageStage.Ready : PackageStage.Preparing;
                    package.UpgradeTarget = null;
                    package.ReviewReason = null;
                }
                jobId = existing.Id;
                return Persist();
            }
            var job = NewJob(path, PackageStage.Preparing);
            _jobs.Add(job);
            jobId = job.Id;
            return Persist();
        }
    }

    // These helpers preserve the legacy ready-to-send queue contract.
    public bool Add(string path)
    {
        lock (_gate)
        {
            Load();
            if (!Contains(path))
                _jobs.Add(NewJob(Path.GetFullPath(path), PackageStage.Offline));
            return Persist();
        }
    }

    public bool Contains(string path) => SnapshotJobs().Any(job => job.Packages?.Any(package =>
        SamePath(package.Path, path) && package.Stage == PackageStage.Offline) == true);

    public IReadOnlyList<string> Snapshot() => SnapshotJobs().SelectMany(job => job.Packages ?? [])
        .Where(package => package.Stage == PackageStage.Offline).Select(package => package.Path).ToList();

    public bool Remove(string path)
    {
        lock (_gate)
        {
            Load();
            foreach (var package in _jobs.SelectMany(job => job.Packages ?? []))
                if (SamePath(package.Path, path))
                    package.Stage = PackageStage.Completed;
            RemoveFinishedJobs();
            return Persist();
        }
    }

    internal IReadOnlyList<PendingJob> SnapshotJobs()
    {
        lock (_gate)
        {
            Load();
            return _jobs.Select(job => job.Clone()).ToList();
        }
    }

    internal PendingJob? Get(Guid id) => SnapshotJobs().FirstOrDefault(job => job.Id == id);

    internal bool Update(Guid id, Action<PendingJob> update)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(candidate => candidate.Id == id);
            if (job is null)
                return !_needsPersistence || Persist();
            update(job);
            RemoveFinishedJobs();
            return Persist();
        }
    }

    internal bool UpdatePackage(Guid id, int index, Action<PendingPackage> update) =>
        Update(id, job => update(job.Packages![index]));

    /// <summary>Never leave an in-memory external-operation marker when its durable write failed.</summary>
    internal bool PrepareExternalOperation(Guid id, int index, Action<PendingPackage> update)
    {
        lock (_gate)
        {
            var job = _jobs.Single(item => item.Id == id);
            var before = job.Packages![index].Clone();
            if (UpdatePackage(id, index, update)) return true;
            job.Packages[index] = before;
            return false;
        }
    }

    internal bool Cancel(Guid id) => Update(id, job =>
    {
        job.Packages ??= [];
        foreach (var package in job.Packages)
            if (package.Stage is not (PackageStage.Completed or PackageStage.Review or PackageStage.Sending))
                package.Stage = PackageStage.Skipped;
    });

    internal bool NeedsPersistence { get { lock (_gate) return _needsPersistence; } }
    internal bool Flush() { lock (_gate) return !_needsPersistence || Persist(); }

    private void RemoveFinishedJobs() => _jobs.RemoveAll(job => job.Packages is not null &&
        job.Packages.All(package => package.Stage is PackageStage.Completed or PackageStage.Skipped));

    private static PendingJob NewJob(string path, PackageStage stage) => new()
    {
        Id = Guid.NewGuid(), SourcePath = path,
        Packages = stage == PackageStage.Preparing && ModFileTypes.IsArchive(path)
            ? null : [new PendingPackage { Path = path, Stage = stage }]
    };

    private bool Persist()
    {
        var temp = filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new QueueJournal { Jobs = _jobs }, JsonOptions);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, filePath, overwrite: true);
            _needsPersistence = false;
            return true;
        }
        catch (Exception ex)
        {
            _needsPersistence = true;
            Log.Warn($"Could not save the job journal to {filePath}.", ex);
            try { File.Delete(temp); } catch { }
            return false;
        }
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private sealed class QueueJournal
    {
        public int Version { get; set; } = 1;
        public List<PendingJob> Jobs { get; set; } = [];
    }
}

internal enum PackageStage { Preparing, Upgrading, AwaitingDecision, Ready, Offline, Sending, Review, Completed, Skipped }

internal sealed class PendingJob
{
    public Guid Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public List<string>? SelectedEntries { get; set; }
    public List<PendingPackage>? Packages { get; set; }
    public string? ReviewReason { get; set; }
    public PendingJob Clone() => new()
    {
        Id = Id, SourcePath = SourcePath, ReviewReason = ReviewReason, SelectedEntries = SelectedEntries is null ? null : [.. SelectedEntries],
        Packages = Packages?.Select(package => package.Clone()).ToList()
    };
}

internal sealed class PendingPackage
{
    public string Path { get; set; } = string.Empty;
    public PackageStage Stage { get; set; }
    public string? UpgradeTarget { get; set; }
    public UpgradeResult? UpgradeFailure { get; set; }
    public string? ReviewReason { get; set; }
    public PendingPackage Clone() => (PendingPackage)MemberwiseClone();
}
