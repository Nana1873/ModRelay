using System.Diagnostics;
using System.IO.Pipes;
using ModRelay.Core;

namespace ModRelay.App;

internal sealed class TrayApp : ApplicationContext, IUserInteraction
{
    private readonly string _pipeName;
    private readonly ConfigStore _configStore;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _queueItem;
    private readonly ToolStripMenuItem _reviewItem;
    private readonly ToolStripMenuItem _cancelItem;
    private readonly ToolStripMenuItem _updateAvailableItem;
    private readonly DownloadWatcher _watcher = new();
    private readonly HttpClient _httpClient = new();
    private readonly PendingQueue _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Control _dispatcher = new();
    private readonly ModPipeline _pipeline;
    private readonly PenumbraClient _penumbra;
    private readonly UpdateChecker _updateChecker;

    private AppConfig _config;
    private bool _exiting;
    private bool _setupPending;
    private SettingsForm? _settingsForm;
    private ArchiveProgressForm? _archiveProgressForm;
    private Form? _pipelineDialog;
    private string _activityText = "Ready";
    private int _updateCheckRunning;
    private string? _pendingUpdateUrl;
    private Version? _pendingUpdateVersion;

    public TrayApp(string pipeName, IReadOnlyList<string> initialFiles)
    {
        _pipeName = pipeName;
        _configStore = new ConfigStore();
        var firstRun = !File.Exists(_configStore.FilePath);
        _setupPending = firstRun;

        Log.Init(AppPaths.LogDirectory);
        _config = _configStore.Load();
        _updateChecker = UpdateChecker.ForCurrentApp(_httpClient);
        if (string.IsNullOrWhiteSpace(_config.TexToolsConsolePath))
            _config.TexToolsConsolePath = TexToolsUpgrader.Locate() ?? string.Empty;

        _dispatcher.CreateControl();
        _ = _dispatcher.Handle;

        _trayMenu = new ContextMenuStrip { Font = UiTheme.Font() };
        _statusItem = new ToolStripMenuItem("Ready") { Enabled = false };
        _queueItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _reviewItem = new ToolStripMenuItem("Imports need review — open log", null, (_, _) => OpenLog()) { Visible = false };
        _cancelItem = new ToolStripMenuItem("Cancel current operation", null, (_, _) => CancelCurrentOperation()) { Enabled = false };
        var settings = new ToolStripMenuItem("Open settings", null, (_, _) => ShowSettings());
        settings.Font = new Font(settings.Font, FontStyle.Bold);
        _pauseItem = new ToolStripMenuItem("Pause watching", null, (_, _) => TogglePause()) { CheckOnClick = true };
        var import = new ToolStripMenuItem("Import a mod package…", null, (_, _) => ImportPackage());
        var checkPenumbra = new ToolStripMenuItem("Check Penumbra connection", null, async (_, _) => await CheckPenumbraAsync());
        var checkUpdates = new ToolStripMenuItem("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(silent: false));
        _updateAvailableItem = new ToolStripMenuItem("Update available", null, (_, _) =>
        {
            if (_pendingUpdateUrl is not null)
                OpenUrl(_pendingUpdateUrl);
        })
        {
            Visible = false
        };
        var openLog = new ToolStripMenuItem("Open log", null, (_, _) => OpenLog());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => Exit());
        _trayMenu.Items.AddRange([
            _statusItem,
            _queueItem,
            _reviewItem,
            new ToolStripSeparator(),
            settings,
            import,
            _pauseItem,
            _cancelItem,
            checkPenumbra,
            checkUpdates,
            _updateAvailableItem,
            new ToolStripSeparator(),
            openLog,
            exit
        ]);
        UiTheme.Apply(_trayMenu, _config.DarkMode);

        _trayIcon = new NotifyIcon
        {
            Text = AppPaths.AppName,
            Icon = AppIcon.Current,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        _penumbra = new PenumbraClient(_httpClient, () => _config);
        _pipeline = new ModPipeline(
            () => _config,
            new ArchiveExtractor(),
            new TexToolsUpgrader(),
            _penumbra,
            _pending,
            this,
            path => _watcher.Ignore(path));

        _watcher.FileReady += _pipeline.Enqueue;
        _watcher.FolderStatusesChanged += _ => OnUi(RefreshStatus);
        _pipeline.StateChanged += _ => OnUi(RefreshStatus);
        _pipeline.Start();
        if (firstRun)
            Status("Complete setup to start watching");
        else
            RestartWatcher();

        foreach (var file in initialFiles)
            SubmitExternalFile(file);

        _ = ListenForCommandsAsync(_shutdown.Token);
        if (_config.AutoCheckForUpdates)
            _ = CheckForUpdatesAfterStartupAsync();
        Log.Info("ModRelay started.");

        if (firstRun)
        {
            _configStore.Save(_config);
            var timer = new System.Windows.Forms.Timer { Interval = 350 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                ShowSettings();
            };
            timer.Start();
        }
    }

    private void ShowSettings()
    {
        OnUi(() =>
        {
            if (_settingsForm is { IsDisposed: false } existing)
            {
                WindowActivation.ShowAndActivate(existing);
                Log.Info("Existing settings window activated.");
                return;
            }

            var form = new SettingsForm(_config);
            _settingsForm = form;
            form.UpdateWatchStatus(_watcher.FolderStatuses, _watcher.Paused, _setupPending);
            if (_pendingUpdateUrl is not null && _pendingUpdateVersion is not null)
                form.ShowAvailableUpdate(_pendingUpdateVersion, _pendingUpdateUrl);
            form.ConfigChanged += ApplySettings;
            form.FormClosed += (_, _) =>
            {
                form.ConfigChanged -= ApplySettings;
                if (ReferenceEquals(_settingsForm, form))
                    _settingsForm = null;
                form.Dispose();
                if (!_exiting)
                {
                    if (_setupPending)
                    {
                        _setupPending = false;
                        RestartWatcher();
                    }
                }
            };
            WindowActivation.ShowAndActivate(form);
            Log.Info("Settings window shown.");
        });
    }

    private void ApplySettings(AppConfig updated)
    {
        var previous = _config;
        var startupApplied = false;
        var associationsApplied = false;
        var settingsCommitted = false;
        try
        {
            var startupChanged = previous.RunOnStartup != updated.RunOnStartup;
            var associationsChanged = previous.AssociateFileTypes != updated.AssociateFileTypes;
            var foldersChanged = !SameFolders(previous.WatchFolders, updated.WatchFolders);
            var updatesEnabled = !previous.AutoCheckForUpdates && updated.AutoCheckForUpdates;

            if (startupChanged || associationsChanged)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The application path could not be determined.");
                if (startupChanged)
                {
                    startupApplied = true;
                    StartupRegistration.SetEnabled(updated.RunOnStartup, executable);
                }
                if (associationsChanged)
                {
                    associationsApplied = true;
                    FileAssociationRegistration.SetEnabled(updated.AssociateFileTypes, executable);
                }
            }

            _configStore.Save(updated);
            _config = updated;
            settingsCommitted = true;
            if (foldersChanged && !_setupPending)
                RestartWatcher();
            UiTheme.Apply(_trayMenu, _config.DarkMode);
            RefreshStatus();

            if (updatesEnabled)
                _ = CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            if (!settingsCommitted)
                TryRestoreRegistrations(previous, startupApplied, associationsApplied);
            _settingsForm?.RestoreConfig(_config);
            Log.Error("The settings could not be saved completely.", ex);
            Notify("Settings could not be saved", ex.Message, isError: true);
        }
    }

    private static void TryRestoreRegistrations(
        AppConfig previous,
        bool restoreStartup,
        bool restoreAssociations)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (executable is null)
                return;
            if (restoreAssociations)
                FileAssociationRegistration.SetEnabled(previous.AssociateFileTypes, executable);
            if (restoreStartup)
                StartupRegistration.SetEnabled(previous.RunOnStartup, executable);
        }
        catch (Exception rollbackError)
        {
            Log.Error("Could not roll back a partial Windows registration change.", rollbackError);
        }
    }

    private static bool SameFolders(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count == right.Count && !left.Except(right, StringComparer.OrdinalIgnoreCase).Any();

    private void RestartWatcher()
    {
        _watcher.Start(_config.WatchFolders);
        _watcher.Paused = _pauseItem.Checked;
        Status(_pauseItem.Checked ? "Paused" : "Ready");
    }

    private void TogglePause()
    {
        _watcher.Paused = _pauseItem.Checked;
        _pauseItem.Text = _pauseItem.Checked ? "Resume watching" : "Pause watching";
        Status(_pauseItem.Checked ? "Paused" : "Ready");
    }

    private void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var target = Log.CurrentFile is { } file && File.Exists(file) ? file : AppPaths.LogDirectory;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Could not open the log", ex.Message, isError: true);
        }
    }

    private void ImportPackage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a mod package",
            Filter = "Supported mod files|*.ttmp;*.ttmp2;*.pmp;*.pcp;*.zip;*.7z;*.rar|All files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        foreach (var file in dialog.FileNames)
            SubmitExternalFile(file);
    }

    private async Task CheckPenumbraAsync()
    {
        Status("Checking Penumbra…");
        var reachable = await _penumbra.IsReachableAsync(_shutdown.Token);
        Notify(reachable ? "Penumbra connected" : "Penumbra unavailable",
            reachable
                ? "The local Penumbra HTTP API is ready."
                : "Start FFXIV and enable Penumbra's HTTP API under Settings → Advanced.",
            isError: !reachable);
        Status(_pauseItem.Checked ? "Paused" : "Ready");
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), _shutdown.Token);
            if (_config.AutoCheckForUpdates)
                await CheckForUpdatesAsync(silent: true);
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (Interlocked.CompareExchange(ref _updateCheckRunning, 1, 0) != 0)
            return;

        try
        {
            if (!silent)
                Status("Checking for updates…");

            var result = await _updateChecker.CheckAsync(_shutdown.Token);
            switch (result.Status)
            {
                case UpdateStatus.Available:
                    var latestVersion = result.LatestVersion!;
                    var releaseUrl = result.ReleaseUrl!;
                    _pendingUpdateUrl = releaseUrl;
                    _pendingUpdateVersion = latestVersion;
                    OnUi(() =>
                    {
                        _updateAvailableItem.Text = $"Update {AppVersion.Format(latestVersion)} available…";
                        _updateAvailableItem.Visible = true;
                        _settingsForm?.ShowAvailableUpdate(latestVersion, releaseUrl);
                    });
                    break;

                case UpdateStatus.Current:
                    ClearAvailableUpdate();
                    if (!silent)
                        Notify("ModRelay is up to date", $"You are using {AppVersion.Format(result.CurrentVersion)}.");
                    break;

                case UpdateStatus.Unavailable when !silent:
                    Notify("Updates unavailable", result.Message ?? "No release feed is configured.");
                    break;

                case UpdateStatus.Failed when !silent:
                    Notify("Update check failed", result.Message ?? "The release feed could not be reached.", isError: true);
                    break;

                case UpdateStatus.Failed:
                    Log.Warn($"Automatic update check failed: {result.Message}");
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckRunning, 0);
            if (!silent)
                Status(_pauseItem.Checked ? "Paused" : "Ready");
        }
    }

    private void ClearAvailableUpdate()
    {
        _pendingUpdateUrl = null;
        _pendingUpdateVersion = null;
        OnUi(() =>
        {
            _updateAvailableItem.Visible = false;
            _settingsForm?.ClearAvailableUpdate();
        });
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task ListenForCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(cancellationToken) is { } path)
                {
                    if (path == Program.ShowSettingsCommand)
                        ShowSettings();
                    else
                        SubmitExternalFile(path);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("A second instance could not hand over its file.", ex);
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private void SubmitExternalFile(string path)
    {
        if (!File.Exists(path) || (!ModFileTypes.IsModFile(path) && !ModFileTypes.IsArchive(path)))
            return;

        Log.Info($"File submitted externally: {path}");
        _pipeline.Enqueue(path);
    }

    public Task<IReadOnlyList<string>> SelectArchiveEntriesAsync(
        string archivePath,
        IReadOnlyList<ArchiveEntryInfo> entries,
        CancellationToken cancellationToken = default) =>
        OnUiAsync<IReadOnlyList<string>>(() =>
        {
            using var form = new ArchiveSelectionForm(archivePath, entries, _config.DarkMode);
            return ShowPipelineDialog(form, cancellationToken) == DialogResult.OK ? form.SelectedKeys : [];
        }, cancellationToken);

    public Task BeginArchiveProgressAsync(string archiveName, string message) =>
        OnUiAsync(() =>
        {
            _archiveProgressForm?.Close();
            _archiveProgressForm = new ArchiveProgressForm(archiveName, message, _config.DarkMode);
            _archiveProgressForm.CancelRequested += _pipeline.CancelCurrent;
            _archiveProgressForm.FormClosed += (_, _) => _archiveProgressForm = null;
            _archiveProgressForm.ShowOn(WindowActivation.ForegroundScreen());
            return true;
        });

    public void UpdateArchiveProgress(string message) =>
        OnUi(() => _archiveProgressForm?.UpdateMessage(message));

    public Task EndArchiveProgressAsync() =>
        OnUiAsync(() =>
        {
            _archiveProgressForm?.Close();
            _archiveProgressForm = null;
            return true;
        });

    public Task<bool> ConfirmInstallWithoutUpgradeAsync(
        string fileName, UpgradeResult result, CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            using var form = new UpgradeConfirmationForm(fileName, result, _config.DarkMode);
            return ShowPipelineDialog(form, cancellationToken) == DialogResult.Yes;
        }, cancellationToken);

    private DialogResult ShowPipelineDialog(Form form, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pipelineDialog = form;
        using var cancellation = cancellationToken.Register(() => OnUi(() =>
        {
            if (!form.IsDisposed)
                form.Close();
        }));
        try
        {
            var result = form.ShowDialog();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _pipelineDialog = null;
        }
    }

    private void CancelCurrentOperation() => _pipeline.CancelCurrent();

    public void Notify(string title, string message, bool isError = false)
    {
        OnUi(() =>
        {
            if (!_trayIcon.Visible)
                return;
            _trayIcon.ShowBalloonTip(5000, title, message, isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
        });
    }

    public void Status(string message)
    {
        OnUi(() =>
        {
            _activityText = message;
            RefreshStatus();
        });
    }

    private void RefreshStatus()
    {
        if (_exiting)
            return;
        var state = _pipeline.State;
        var folders = _watcher.FolderStatuses;
        var activity = (state.IsBusy || _activityText.StartsWith("Checking ", StringComparison.Ordinal)) &&
                       _activityText is not ("Ready" or "Paused") ? _activityText : null;
        var watching = TrayStatusText.Watching(folders, _watcher.Paused, _setupPending);
        _statusItem.Text = state.SavePending ? "Queue not saved — keep ModRelay running"
            : activity ?? (state.IsBusy ? $"Processing {state.CurrentFile}" : watching);
        _queueItem.Text = TrayStatusText.Queue(state.QueuedCount, state.PendingCount);
        _queueItem.Visible = state.QueuedCount > 0;
        _reviewItem.Text = $"{state.ReviewCount} import(s) need review — open log";
        _reviewItem.Visible = state.ReviewCount > 0;
        _cancelItem.Enabled = state.CanCancel;
        var summary = state.SavePending ? string.Empty
            : state.ReviewCount > 0 ? $"{state.ReviewCount} need review · "
            : state.QueuedCount > 0 ? $"{_queueItem.Text} · " : string.Empty;
        _trayIcon.Text = Shorten($"{AppPaths.AppName} – {summary}{_statusItem.Text}", 63);
        _settingsForm?.UpdateWatchStatus(folders, _watcher.Paused, _setupPending);
    }

    private void OnUi(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed)
            return;
        try
        {
            if (_dispatcher.InvokeRequired)
                _dispatcher.BeginInvoke(() =>
                {
                    if (!_exiting && !_dispatcher.IsDisposed)
                        action();
                });
            else
                action();
        }
        catch (InvalidOperationException) when (_exiting || _dispatcher.IsDisposed)
        {
            // Shutdown can dispose the dispatcher between the check and BeginInvoke.
        }
    }

    private async Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (_exiting || _dispatcher.IsDisposed)
            throw new OperationCanceledException(new CancellationToken(canceled: true));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = _shutdown.Token.Register(() => completion.TrySetCanceled());
        using var operationCancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        OnUi(() =>
        {
            if (completion.Task.IsCompleted)
                return;
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task;
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private void Exit()
    {
        _exiting = true;
        _shutdown.Cancel();
        _settingsForm?.Close();
        _pipelineDialog?.Close();
        _archiveProgressForm?.Close();
        _watcher.Dispose();
        _pipeline.Dispose();
        _httpClient.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _dispatcher.Dispose();
        _shutdown.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
            Exit();
        base.Dispose(disposing);
    }
}
