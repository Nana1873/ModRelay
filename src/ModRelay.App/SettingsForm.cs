using System.Diagnostics;
using ModRelay.Core;

namespace ModRelay.App;

internal sealed class SettingsForm : SmoothDpiForm
{
    private readonly CheckBox _notifications = Toggle("Successful imports");
    private readonly CheckBox _errorNotifications = Toggle("Import failures and problems");
    private readonly CheckBox _trayNotifications = Toggle("Minimized to tray");
    private readonly CheckBox _notificationSounds = Toggle("Play notification sounds");
    private readonly CheckBox _autoForward = Toggle("Automatically send mods to Penumbra");
    private readonly CheckBox _extractAll = Toggle("Extract every mod from archives");
    private readonly CheckBox _runOnStartup = Toggle("Start with Windows");
    private readonly CheckBox _autoDelete = Toggle("Delete processed downloads");
    private readonly CheckBox _autoUpgrade = Toggle("Upgrade Endwalker mods to Dawntrail");
    private readonly CheckBox _associateFiles = Toggle("Open mod files with ModRelay");
    private readonly CheckBox _installOnFailure = Toggle("Install the original when an upgrade fails");
    private readonly CheckBox _darkMode = Toggle("Use dark mode");
    private readonly CheckBox _autoCheckUpdates = Toggle("Check for updates automatically");

    private readonly ListBox _watchFolders = new();
    private readonly TextBox _texToolsPath = new();
    private readonly Label _texToolsStatus = new();
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 350 };
    private readonly List<(Button Button, Control Page)> _pages = [];
    private readonly TableLayoutPanel _updateBanner = new();
    private readonly Label _updateText = new();
    private string? _updateUrl;
    private Panel? _pageHost;

    public AppConfig ResultConfig { get; private set; }
    public event Action<AppConfig>? ConfigChanged;

    public SettingsForm(AppConfig config)
    {
        ResultConfig = config.Clone();
        Text = "ModRelay – Settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 540);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = UiTheme.Background;
        Font = UiTheme.Font();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Icon = AppIcon.Current;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildUpdateBanner(), 0, 1);
        root.Controls.Add(BuildContent(), 0, 2);
        Controls.Add(root);

        LoadConfig(config);
        _darkMode.CheckedChanged += (_, _) => ApplyTheme(_darkMode.Checked);
        WireAutoSave();
        HandleCreated += (_, _) => UiTheme.ApplyTitleBar(this, _darkMode.Checked);
        ApplyTheme(config.DarkMode);
        UpdateTexToolsStatus();
    }

    internal bool HasAvailableUpdate => !string.IsNullOrWhiteSpace(_updateUrl);

    public void ShowAvailableUpdate(Version version, string releaseUrl)
    {
        _updateUrl = releaseUrl;
        _updateText.Text = $"ModRelay {AppVersion.Format(version)} is available. Current: {AppVersion.Current}.";
        _updateBanner.Visible = true;
    }

    public void ClearAvailableUpdate()
    {
        _updateUrl = null;
        _updateBanner.Visible = false;
    }

    private Control BuildUpdateBanner()
    {
        _updateBanner.Dock = DockStyle.Fill;
        _updateBanner.AutoSize = true;
        _updateBanner.ColumnCount = 2;
        _updateBanner.RowCount = 1;
        _updateBanner.Padding = new Padding(14, 7, 14, 7);
        _updateBanner.Margin = Padding.Empty;
        _updateBanner.Tag = "update-banner";
        _updateBanner.Visible = false;
        _updateBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _updateBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _updateText.AutoSize = true;
        _updateText.Dock = DockStyle.Fill;
        _updateText.TextAlign = ContentAlignment.MiddleLeft;
        _updateText.Font = UiTheme.Font(9, FontStyle.Bold);
        _updateText.Margin = new Padding(0, 5, 8, 0);
        var view = UiTheme.Button("View update", primary: true);
        view.MinimumSize = new Size(104, 30);
        view.Click += (_, _) =>
        {
            if (_updateUrl is not null)
                OpenUrl(_updateUrl);
        };
        _updateBanner.Controls.Add(_updateText, 0, 0);
        _updateBanner.Controls.Add(view, 1, 0);
        return _updateBanner;
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Header,
            Padding = new Padding(18, 8, 18, 8),
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Tag = "header"
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = $"ModRelay {AppVersion.Current}",
            ForeColor = Color.White,
            Font = UiTheme.Font(15, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        });
        panel.Controls.Add(new Label
        {
            Text = "Watch downloads, upgrade when needed, and relay mods to Penumbra. Changes save automatically.",
            ForeColor = Color.FromArgb(190, 196, 214),
            Font = UiTheme.Font(9),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(1, 0, 0, 0)
        });
        return panel;
    }

    private Control BuildContent()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14, 8, 14, 8),
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var navigation = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _pageHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        AddPage(navigation, "General", BuildPage(BuildSwitchCard(), BuildFoldersCard()));
        AddPage(navigation, "Connections", BuildPage(BuildTexToolsCard(), BuildPenumbraCard()));
        AddPage(navigation, "Advanced", BuildPage(BuildNotificationCard(), BuildAdvancedCard()));
        root.Controls.Add(navigation, 0, 0);
        root.Controls.Add(_pageHost, 0, 1);
        ShowPage(_pages[0].Page);
        return root;
    }

    private void AddPage(Control navigation, string title, Control page)
    {
        var button = UiTheme.Button(title);
        button.Name = $"PageTab{title}";
        button.MinimumSize = new Size(116, 30);
        button.Margin = new Padding(0, 0, 2, 6);
        button.Click += (_, _) => ShowPage(page);
        navigation.Controls.Add(button);
        _pageHost!.Controls.Add(page);
        _pages.Add((button, page));
    }

    private static Control BuildPage(params Control[] sections)
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0), Visible = false };
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = sections.Length + 1 };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var section in sections)
        {
            section.Dock = DockStyle.Top;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(section);
        }
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(stack);
        return page;
    }

    private void ShowPage(Control page)
    {
        foreach (var entry in _pages)
            entry.Page.Visible = ReferenceEquals(entry.Page, page);
        page.BringToFront();
        StylePageButtons();
    }

    private void StylePageButtons()
    {
        foreach (var entry in _pages)
        {
            var selected = entry.Page.Visible;
            entry.Button.BackColor = selected
                ? UiTheme.Accent
                : _darkMode.Checked ? UiTheme.DarkSurface : UiTheme.Surface;
            entry.Button.ForeColor = selected
                ? Color.White
                : _darkMode.Checked ? UiTheme.DarkMuted : UiTheme.Muted;
            entry.Button.FlatAppearance.BorderColor = selected
                ? UiTheme.Accent
                : _darkMode.Checked ? UiTheme.DarkBorder : UiTheme.Border;
        }
    }

    private Control BuildSwitchCard()
    {
        var card = CardWithTitle("Automation", "Choose what ModRelay handles automatically.");
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0, 6, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var toggles = new[] { _notifications, _autoForward, _extractAll, _runOnStartup, _autoDelete, _autoUpgrade };
        for (var i = 0; i < toggles.Length; i++)
        {
            toggles[i].Dock = DockStyle.Fill;
            grid.Controls.Add(toggles[i], i % 2, i / 2);
        }
        AddCardBody(card, grid);
        return card;
    }

    private Control BuildTexToolsCard()
    {
        var card = CardWithTitle("Dawntrail upgrade", "TexTools converts older mod packs before they reach Penumbra.");
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Padding = new Padding(0, 6, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _texToolsPath.Dock = DockStyle.Fill;
        _texToolsPath.PlaceholderText = "Path to ConsoleTools.exe";
        var browse = UiTheme.Button("Browse");
        browse.Click += (_, _) => BrowseForFile(_texToolsPath, "ConsoleTools.exe|ConsoleTools.exe");
        var detect = UiTheme.Button("Detect automatically");
        detect.Click += (_, _) =>
        {
            _texToolsPath.Text = TexToolsUpgrader.Locate() ?? string.Empty;
            UpdateTexToolsStatus();
        };
        layout.Controls.Add(_texToolsPath, 0, 0);
        layout.Controls.Add(browse, 1, 0);
        layout.Controls.Add(detect, 2, 0);
        _texToolsStatus.AutoSize = true;
        _texToolsStatus.Margin = new Padding(0, 5, 0, 0);
        layout.Controls.Add(_texToolsStatus, 0, 1);
        layout.SetColumnSpan(_texToolsStatus, 2);
        var download = new LinkLabel { Text = "Download TexTools", AutoSize = true, Margin = new Padding(8, 5, 0, 0), LinkColor = UiTheme.Accent };
        download.LinkClicked += (_, _) => OpenUrl(TexToolsUpgrader.DownloadUrl);
        layout.Controls.Add(download, 2, 1);
        AddCardBody(card, layout);
        _texToolsPath.TextChanged += (_, _) => UpdateTexToolsStatus();
        return card;
    }

    private Control BuildFoldersCard()
    {
        var card = CardWithTitle("Watched folders", "Detect mod files and archives when downloads finish.");
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(0, 6, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _watchFolders.Height = 66;
        _watchFolders.Dock = DockStyle.Fill;
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        var add = UiTheme.Button("Add folder");
        add.Click += (_, _) => AddWatchFolder();
        var remove = UiTheme.Button("Remove");
        remove.Click += (_, _) =>
        {
            if (_watchFolders.SelectedIndex < 0)
                return;

            _watchFolders.Items.RemoveAt(_watchFolders.SelectedIndex);
            SaveNow();
        };
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        layout.Controls.Add(_watchFolders, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        AddCardBody(card, layout);
        return card;
    }

    private Control BuildPenumbraCard()
    {
        return CardWithTitle(
            "Penumbra",
            "Enable its HTTP API under Settings → Advanced. ModRelay uses Penumbra's official localhost:42069 endpoint.");
    }

    private Control BuildAdvancedCard()
    {
        var card = CardWithTitle("More options", "File associations apply only to your Windows account.");
        var stack = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0, 5, 0, 0) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        stack.Controls.Add(_darkMode, 0, 0);
        stack.Controls.Add(_autoCheckUpdates, 1, 0);
        stack.Controls.Add(_associateFiles, 0, 1);
        _installOnFailure.ForeColor = Color.FromArgb(170, 75, 60);
        _installOnFailure.Tag = "danger";
        stack.Controls.Add(_installOnFailure, 0, 2);
        stack.SetColumnSpan(_installOnFailure, 2);
        AddCardBody(card, stack);
        return card;
    }

    private Control BuildNotificationCard()
    {
        var card = CardWithTitle("Notifications", "Choose which events may appear in Windows and whether they make a sound.");
        var stack = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0, 5, 0, 0) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        stack.Controls.Add(_notifications, 0, 0);
        stack.Controls.Add(_errorNotifications, 1, 0);
        stack.Controls.Add(_trayNotifications, 0, 1);
        stack.Controls.Add(_notificationSounds, 1, 1);
        AddCardBody(card, stack);
        return card;
    }

    private void LoadConfig(AppConfig config)
    {
        _notifications.Checked = config.ShowNotifications;
        _errorNotifications.Checked = config.ShowErrorNotifications;
        _trayNotifications.Checked = config.ShowTrayNotifications;
        _notificationSounds.Checked = config.PlayNotificationSounds;
        _autoForward.Checked = config.AutoForwardToPenumbra;
        _extractAll.Checked = config.ExtractAllMods;
        _runOnStartup.Checked = config.RunOnStartup;
        _autoDelete.Checked = config.AutoDeleteMods;
        _autoUpgrade.Checked = config.AutoUpgradeToDawntrail;
        _associateFiles.Checked = config.AssociateFileTypes;
        _installOnFailure.Checked = config.InstallOriginalWhenUpgradeFails;
        _darkMode.Checked = config.DarkMode;
        _autoCheckUpdates.Checked = config.AutoCheckForUpdates;
        _texToolsPath.Text = config.TexToolsConsolePath;
        foreach (var folder in config.WatchFolders)
            _watchFolders.Items.Add(folder);
    }

    private void WireAutoSave()
    {
        _saveTimer.Tick += (_, _) => SaveNow();
        foreach (var toggle in new[]
                 {
                     _notifications, _errorNotifications, _trayNotifications, _notificationSounds,
                     _autoForward, _extractAll, _runOnStartup, _autoDelete,
                     _autoUpgrade, _associateFiles, _installOnFailure, _darkMode, _autoCheckUpdates
                 })
            toggle.CheckedChanged += (_, _) => SaveNow();

        _texToolsPath.TextChanged += (_, _) => QueueSave();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        var folders = _watchFolders.Items.Cast<string>()
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ResultConfig.ShowNotifications = _notifications.Checked;
        ResultConfig.ShowErrorNotifications = _errorNotifications.Checked;
        ResultConfig.ShowTrayNotifications = _trayNotifications.Checked;
        ResultConfig.PlayNotificationSounds = _notificationSounds.Checked;
        ResultConfig.AutoForwardToPenumbra = _autoForward.Checked;
        ResultConfig.ExtractAllMods = _extractAll.Checked;
        ResultConfig.RunOnStartup = _runOnStartup.Checked;
        ResultConfig.AutoDeleteMods = _autoDelete.Checked;
        ResultConfig.AutoUpgradeToDawntrail = _autoUpgrade.Checked;
        ResultConfig.AssociateFileTypes = _associateFiles.Checked;
        ResultConfig.InstallOriginalWhenUpgradeFails = _installOnFailure.Checked;
        ResultConfig.DarkMode = _darkMode.Checked;
        ResultConfig.AutoCheckForUpdates = _autoCheckUpdates.Checked;
        ResultConfig.WatchFolders = folders;
        ResultConfig.TexToolsConsolePath = _texToolsPath.Text.Trim();
        ConfigChanged?.Invoke(ResultConfig.Clone());
    }

    private void UpdateTexToolsStatus()
    {
        var valid = File.Exists(_texToolsPath.Text);
        _texToolsStatus.Text = valid ? "● Ready for Dawntrail upgrades" : "● ConsoleTools.exe is not configured yet";
        _texToolsStatus.ForeColor = valid ? UiTheme.Success : Color.FromArgb(184, 115, 36);
    }

    private void ApplyTheme(bool dark)
    {
        UiTheme.Apply(this, dark);
        UpdateTexToolsStatus();
        StylePageButtons();
        Invalidate(true);
    }

    private void AddWatchFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a download folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK && !_watchFolders.Items.Cast<string>().Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _watchFolders.Items.Add(dialog.SelectedPath);
            SaveNow();
        }
    }

    private void BrowseForFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (File.Exists(target.Text)) dialog.InitialDirectory = Path.GetDirectoryName(target.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
    }

    private static Panel CardWithTitle(string title, string description)
    {
        var card = UiTheme.Card();
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = UiTheme.Text, Font = UiTheme.Font(11, FontStyle.Bold), Dock = DockStyle.Top }, 0, 0);
        layout.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = UiTheme.Muted, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 0) }, 0, 1);
        card.Controls.Add(layout);
        card.Tag = layout;
        return card;
    }

    private static void AddCardBody(Panel card, Control body)
    {
        var layout = (TableLayoutPanel)card.Tag!;
        layout.Controls.Add(body, 0, 2);
    }

    private static CheckBox Toggle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 25),
        Padding = new Padding(0, 1, 8, 1),
        ForeColor = UiTheme.Text,
        Cursor = Cursors.Hand
    };

    private static Label LabelFor(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Muted, Margin = new Padding(0, 8, 12, 8) };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_saveTimer.Enabled)
            SaveNow();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _saveTimer.Dispose();
        base.OnFormClosed(e);
    }

}
