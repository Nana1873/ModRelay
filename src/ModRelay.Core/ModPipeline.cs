using System.Runtime.Versioning;
using System.Threading.Channels;

namespace ModRelay.Core;

/// <summary>
/// One file at a time: unpack -> upgrade to Dawntrail -> hand to Penumbra -> clean up.
/// Serial by design; two ConsoleTools runs at once just fight over the same disk.
/// </summary>
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

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private Task? _retryTask;
    private Timer? _retryTimer;
    private int _activeOperations;
    private int _retryRunning;

    public ModPipeline(
        Func<AppConfig> configProvider,
        ArchiveExtractor extractor,
        TexToolsUpgrader upgrader,
        PenumbraClient penumbra,
        PendingQueue pending,
        IUserInteraction ui,
        Action<string>? ignoreGeneratedFile = null)
    {
        _config = configProvider;
        _extractor = extractor;
        _upgrader = upgrader;
        _penumbra = penumbra;
        _pending = pending;
        _ui = ui;
        _ignoreGeneratedFile = ignoreGeneratedFile ?? (_ => { });
    }

    public void Start()
    {
        if (!_pending.Load() && _config().ShowErrorNotifications)
            _ui.Notify("Retry queue could not be read",
                "The damaged queue was backed up. Previously queued mods need to be submitted again.", isError: true);
        _worker ??= Task.Run(() => RunAsync(_shutdown.Token));
        _retryTimer ??= new Timer(_ => RetryPending(), null, RetryInterval, RetryInterval);
    }

    public void Enqueue(string path) => _queue.Writer.TryWrite(path);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var path in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            BeginOperation();
            try
            {
                await ProcessAsync(path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Processing {path} failed.", ex);
                if (_config().ShowErrorNotifications)
                    _ui.Notify("Processing failed", $"{Path.GetFileName(path)} could not be processed.", isError: true);
            }
            finally
            {
                EndOperation();
            }
        }
    }

    internal async Task ProcessAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return;

        if (ModFileTypes.IsArchive(path))
        {
            await ProcessArchiveAsync(path, cancellationToken);
            return;
        }

        await ProcessModFileAsync(path, cancellationToken);
    }

    private async Task ProcessArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archivePath);
        _ui.Status($"Inspecting {name}");

        IReadOnlyList<ArchiveEntryInfo> entries;
        try
        {
            entries = _extractor.Inspect(archivePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"{archivePath} is not a readable archive; leaving it alone.", ex);
            if (_config().ShowErrorNotifications)
                _ui.Notify("Archive could not be read",
                    $"{name} is damaged, incomplete, or uses an unsupported archive format.", isError: true);
            return;
        }

        if (entries.Count == 0)
        {
            Log.Info($"No mod files inside {name}; leaving it alone.");
            return;
        }

        var config = _config();

        // Pre-Dawntrail files are never filtered out here - they are exactly the ones
        // the TexTools upgrade exists for.
        var selected = config.ExtractAllMods || entries.Count == 1
            ? entries.Select(e => e.Key).ToList()
            : [.. await _ui.SelectArchiveEntriesAsync(archivePath, entries)];

        if (selected.Count == 0)
        {
            Log.Info($"Nothing selected from {name}.");
            return;
        }

        _ui.Status($"Extracting {name}");
        await _ui.BeginArchiveProgressAsync(name, $"Preparing to extract {selected.Count} selected mod(s)…");

        var destination = Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            Path.GetFileNameWithoutExtension(archivePath));

        IReadOnlyList<string> extracted;
        var currentEntry = 0;
        try
        {
            extracted = _extractor.Extract(
                archivePath, selected, destination,
                new InlineProgress<string>(message =>
                {
                    var current = Interlocked.Increment(ref currentEntry);
                    _ui.Status(message);
                    _ui.UpdateArchiveProgress($"{current} of {selected.Count} — {message}");
                }), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Extraction of {archivePath} was stopped; the archive was kept.", ex);
            if (_config().ShowErrorNotifications)
                _ui.Notify("Archive extraction stopped", ex.Message, isError: true);
            return;
        }
        finally
        {
            await _ui.EndArchiveProgressAsync();
        }

        foreach (var generatedFile in extracted)
            _ignoreGeneratedFile(generatedFile);

        if (config.AutoDeleteMods)
            TryDelete(archivePath);

        foreach (var modFile in extracted)
            await ProcessModFileAsync(modFile, cancellationToken);
    }

    private async Task ProcessModFileAsync(string modPath, CancellationToken cancellationToken)
    {
        var config = _config();

        var pathToInstall = await UpgradeIfWantedAsync(modPath, config, cancellationToken);
        if (pathToInstall is null)
            return;

        if (!config.AutoForwardToPenumbra)
        {
            Log.Info($"Auto-forward is off; {pathToInstall} stays where it is.");
            if (config.ShowNotifications)
                _ui.Notify("Mod ready", Path.GetFileName(pathToInstall));
            return;
        }

        await InstallAsync(pathToInstall, config, cancellationToken);
    }

    /// <summary>
    /// Returns the path to install, or null when the user decided against installing
    /// an un-upgraded mod.
    /// </summary>
    private async Task<string?> UpgradeIfWantedAsync(string modPath, AppConfig config, CancellationToken cancellationToken)
    {
        if (!config.AutoUpgradeToDawntrail || !CanUpgradeWithTexTools(modPath))
            return modPath;

        var fileName = Path.GetFileName(modPath);

        if (string.IsNullOrWhiteSpace(config.TexToolsConsolePath) || !File.Exists(config.TexToolsConsolePath))
        {
            // Only worth bothering the user about when the mod actually looks like it needs it.
            if (!ModFileTypes.LooksPreDawntrail(fileName))
                return modPath;

            var missing = new UpgradeResult(UpgradeStatus.ToolMissing, null, -1, string.Empty);
            return await HandleFailedUpgradeAsync(modPath, fileName, missing, config);
        }

        _ui.Status($"Upgrading {fileName} … (this may take a few minutes)");

        var target = UniqueGeneratedPath(Path.Combine(
            Path.GetDirectoryName(modPath)!,
            Path.GetFileNameWithoutExtension(modPath) + "_dt.ttmp2"));

        _ignoreGeneratedFile(target);

        var result = await _upgrader.UpgradeAsync(config.TexToolsConsolePath, modPath, target, cancellationToken);

        switch (result.Status)
        {
            case UpgradeStatus.Upgraded:
                _ignoreGeneratedFile(result.OutputPath!);
                if (config.ShowNotifications)
                    _ui.Notify("Auf Dawntrail aktualisiert", fileName);

                if (config.AutoDeleteMods)
                    TryDelete(modPath);

                return result.OutputPath;

            case UpgradeStatus.NotNeeded:
                Log.Info($"{fileName} needs no upgrade.");
                return modPath;

            default:
                return await HandleFailedUpgradeAsync(modPath, fileName, result, config);
        }
    }

    private static bool CanUpgradeWithTexTools(string path) =>
        Path.GetExtension(path) is var extension &&
        (extension.Equals(".ttmp", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ttmp2", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> HandleFailedUpgradeAsync(
        string modPath, string fileName, UpgradeResult result, AppConfig config)
    {
        if (config.InstallOriginalWhenUpgradeFails)
        {
            Log.Warn($"Upgrade of {fileName} failed ({result.Status}); installing the original as configured.");
            return modPath;
        }

        var install = await _ui.ConfirmInstallWithoutUpgradeAsync(fileName, result);

        if (install)
            return modPath;

        Log.Info($"{fileName} was not installed - upgrade failed and the user declined.");
        if (config.ShowErrorNotifications)
            _ui.Notify("Not installed", $"{fileName} was not imported.", isError: true);
        return null;
    }

    private async Task InstallAsync(string modPath, AppConfig config, CancellationToken cancellationToken)
    {
        _ui.Status($"Sending {Path.GetFileName(modPath)} to Penumbra");

        var result = await _penumbra.InstallAsync(modPath, cancellationToken);

        switch (result.Outcome)
        {
            case InstallOutcome.Imported:
                if (config.ShowNotifications)
                    _ui.Notify("Mod imported", result.ModName);

                RemoveFromPending(modPath, config);

                if (config.AutoDeleteMods)
                    TryDelete(modPath);
                break;

            case InstallOutcome.Accepted:
                RemoveFromPending(modPath, config);
                if (config.ShowErrorNotifications)
                    _ui.Notify("Import accepted",
                        $"{result.ModName} was queued by Penumbra. Verify it in Penumbra before deleting the retained source file.");
                break;

            case InstallOutcome.PenumbraUnreachable:
                var queueSaved = _pending.Add(modPath);
                if (config.ShowErrorNotifications)
                    _ui.Notify(queueSaved ? "Penumbra is unavailable" : "Retry queue could not be saved",
                        queueSaved
                            ? $"{result.ModName} will be installed as soon as Penumbra is available."
                            : $"Keep ModRelay running or submit {result.ModName} again later; its retry could not be persisted.",
                        isError: !queueSaved);
                break;

            default:
                if (config.ShowErrorNotifications)
                    _ui.Notify("Import failed", result.Message ?? result.ModName, isError: true);
                break;
        }
    }

    private void RemoveFromPending(string modPath, AppConfig config)
    {
        if (_pending.Remove(modPath) || !config.ShowErrorNotifications)
            return;

        _ui.Notify(
            "Retry queue could not be updated",
            $"{Path.GetFileName(modPath)} was processed, but its retry entry could not be removed. " +
            "Keep ModRelay running and restore write access to its data folder before restarting.",
            isError: true);
    }

    private void RetryPending()
    {
        if ((_pending.Count == 0 && !_pending.NeedsPersistence) || _shutdown.IsCancellationRequested ||
            Interlocked.CompareExchange(ref _retryRunning, 1, 0) != 0)
            return;

        _retryTask = Task.Run(async () =>
        {
            BeginOperation();
            try
            {
                _pending.Flush();
                if (_pending.Count == 0)
                    return;

                if (!await _penumbra.IsReachableAsync(_shutdown.Token))
                    return;

                foreach (var path in _pending.Snapshot())
                {
                    if (!File.Exists(path))
                    {
                        _pending.Remove(path);
                        continue;
                    }

                    Log.Info($"Penumbra is back; retrying {path}");
                    await InstallAsync(path, _config(), _shutdown.Token);
                }
            }
            finally
            {
                EndOperation();
                Interlocked.Exchange(ref _retryRunning, 0);
            }
        }, _shutdown.Token);
    }

    private void BeginOperation() => Interlocked.Increment(ref _activeOperations);

    private void EndOperation()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0)
            _ui.Status("Ready");
    }

    private static string UniqueGeneratedPath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Deleting right after a write often hits a lock from an antivirus scanner or the
    /// search indexer, so give it a few tries before giving up.
    /// </summary>
    private static void TryDelete(string path, int attempts = 4)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                File.Delete(path);
                Log.Info($"Deleted {path}");
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(500 * attempt);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete {path}; leaving it in place.", ex);
                return;
            }
        }

        Log.Warn($"Could not delete {path} after {attempts} attempts; leaving it in place.");
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        _retryTimer?.Dispose();
        WaitForShutdown(_worker);
        WaitForShutdown(_retryTask);
        _shutdown.Dispose();
    }

    private static void WaitForShutdown(Task? task)
    {
        if (task is null || task.IsCompleted || Task.CurrentId == task.Id)
            return;

        try { task.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
