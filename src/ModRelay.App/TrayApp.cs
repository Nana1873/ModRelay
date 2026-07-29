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
    private readonly ToolStripMenuItem _updateAvailableItem;
    private readonly DownloadWatcher _watcher = new();
    private readonly HttpClient _httpClient = new();
    private readonly PendingQueue _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Control _dispatcher = new();
    private readonly ModPipeline _pipeline;
    private readonly PenumbraClient _penumbra;
    private readonly UpdateChecker _updateChecker;
    private readonly WindowsNotificationService _notifications;

    private AppConfig _config;
    private bool _exiting;
    private bool _setupPending;
    private SettingsForm? _settingsForm;
    private ArchiveProgressForm? _archiveProgressForm;
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
        var openData = new ToolStripMenuItem("Open ModRelay data folder", null, (_, _) => OpenPath(AppPaths.DataDirectory));
        var debugging = new ToolStripMenuItem("Debugging");
        debugging.DropDownItems.AddRange([openLog, openData]);
        var resources = new ToolStripMenuItem("Resources");
        resources.DropDownItems.Add("Penumbra on GitHub", null, (_, _) => OpenUrl("https://github.com/xivdev/Penumbra"));
        resources.DropDownItems.Add("Download TexTools", null, (_, _) => OpenUrl(TexToolsUpgrader.DownloadUrl));
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => Exit());
        _trayMenu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            settings,
            import,
            _pauseItem,
            checkPenumbra,
            checkUpdates,
            _updateAvailableItem,
            new ToolStripSeparator(),
            debugging,
            resources,
            new ToolStripSeparator(),
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
        _notifications = new WindowsNotificationService(_trayIcon);

        var testNotification = new ToolStripMenuItem("Test notification", null, (_, _) =>
            Notify("ModRelay notifications work", "Windows can show import, error, and tray status notifications."));
        debugging.DropDownItems.Insert(0, testNotification);
        debugging.DropDownItems.Insert(1, new ToolStripSeparator());

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
                    _ = ShowMinimizedNotificationAsync();
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
            Status("Settings saved automatically");

            if (updatesEnabled)
                _ = CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            if (!settingsCommitted)
                TryRestoreRegistrations(previous, startupApplied, associationsApplied);
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

    private async Task ShowMinimizedNotificationAsync()
    {
        try
        {
            await Task.Delay(300, _shutdown.Token);
            if (_config.ShowTrayNotifications)
                Notify("ModRelay was minimized to the tray",
                    "It is still running and watching your download folders.");
        }
        catch (OperationCanceledException)
        {
            // The application exited before the reminder was due.
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(cancellationToken) is { } path)
                {
                    if (path == Program.ShowSettingsCommand)
                        ShowSettings();
                    else if (path == Program.TestNotificationCommand)
                        Notify("ModRelay notifications work", "Windows can show import, error, and tray status notifications.");
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
        IReadOnlyList<ArchiveEntryInfo> entries) =>
        OnUiAsync<IReadOnlyList<string>>(() =>
        {
            using var form = new ArchiveSelectionForm(archivePath, entries, _config.DarkMode);
            return form.ShowDialog() == DialogResult.OK ? form.SelectedKeys : [];
        });

    public Task BeginArchiveProgressAsync(string archiveName, string message) =>
        OnUiAsync(() =>
        {
            _archiveProgressForm?.Close();
            _archiveProgressForm = new ArchiveProgressForm(archiveName, message, _config.DarkMode);
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

    public Task<bool> ConfirmInstallWithoutUpgradeAsync(string fileName, UpgradeResult result) =>
        OnUiAsync(() =>
        {
            var reason = result.Status == UpgradeStatus.ToolMissing
                ? "TexTools ConsoleTools.exe is not configured."
                : $"TexTools could not upgrade the mod (exit code {result.ExitCode}).";
            var answer = MessageBox.Show(
                $"{reason}\n\n{fileName}\n\nSend the unchanged original to Penumbra anyway?",
                "Dawntrail upgrade failed",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            return answer == DialogResult.Yes;
        });

    public void Notify(string title, string message, bool isError = false)
    {
        OnUi(() =>
        {
            if (!_trayIcon.Visible)
                return;
            _notifications.Show(title, message, isError, _config.PlayNotificationSounds);
        });
    }

    public void Status(string message)
    {
        OnUi(() =>
        {
            var displayed = message == "Ready" && _watcher.Paused ? "Paused" : message;
            _statusItem.Text = displayed;
            _trayIcon.Text = Shorten($"{AppPaths.AppName} – {displayed}", 63);
        });
    }

    private void OnUi(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed)
            return;
        if (_dispatcher.InvokeRequired)
            _dispatcher.BeginInvoke(action);
        else
            action();
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (_exiting || _dispatcher.IsDisposed)
            return Task.FromCanceled<T>(new CancellationToken(canceled: true));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private void Exit()
    {
        _exiting = true;
        _shutdown.Cancel();
        _settingsForm?.Close();
        _archiveProgressForm?.Close();
        _watcher.Dispose();
        _pipeline.Dispose();
        _penumbra.Dispose();
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
