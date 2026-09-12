using ModRelay.Core;

namespace ModRelay.App;

internal sealed class ArchiveSelectionForm : SmoothDpiForm
{
    private readonly CheckedListBox _entries = new();
    private readonly IReadOnlyList<ArchiveEntryInfo> _models;

    public ArchiveSelectionForm(string archivePath, IReadOnlyList<ArchiveEntryInfo> entries, bool darkMode)
    {
        _models = entries;
        Text = "Select mods";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        BackColor = UiTheme.Background;
        Font = UiTheme.Font();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Icon = AppIcon.Current;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = "Which mods should be extracted?",
            AutoSize = true,
            Font = UiTheme.Font(15, FontStyle.Bold),
            ForeColor = UiTheme.Text,
            Margin = new Padding(0, 0, 0, 4)
        });
        root.Controls.Add(new Label
        {
            Text = Path.GetFileName(archivePath),
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0, 0, 0, 4)
        });
        root.Controls.Add(new Label
        {
            Text = "Pre-DT markers are inferred from package names.",
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0, 0, 0, 14)
        });

        _entries.Dock = DockStyle.Fill;
        _entries.CheckOnClick = true;
        _entries.BorderStyle = BorderStyle.FixedSingle;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var marker = entry.LooksPreDawntrail ? "[PRE-DT?]  " : string.Empty;
            // The archive key is deliberately shown instead of just FileName. Archives
            // commonly contain variants with the same file name in different folders.
            _entries.Items.Add($"{marker}{entry.Key}", false);
        }
        root.Controls.Add(_entries);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 14, 0, 0)
        };
        var take = UiTheme.Button("Extract selected", primary: true);
        take.DialogResult = DialogResult.OK;
        take.Enabled = false;
        var cancel = UiTheme.Button("Skip");
        cancel.DialogResult = DialogResult.Cancel;
        var clear = UiTheme.Button("Clear");
        clear.Click += (_, _) => SetAllEntriesChecked(false);
        var selectAll = UiTheme.Button("Select all");
        selectAll.Click += (_, _) => SetAllEntriesChecked(true);
        buttons.Controls.Add(take);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(selectAll);
        root.Controls.Add(buttons);

        _entries.ItemCheck += (_, eventArgs) =>
        {
            var selectedCount = _entries.CheckedIndices.Count;
            if (eventArgs.CurrentValue == CheckState.Checked && eventArgs.NewValue != CheckState.Checked)
                selectedCount--;
            else if (eventArgs.CurrentValue != CheckState.Checked && eventArgs.NewValue == CheckState.Checked)
                selectedCount++;
            take.Enabled = selectedCount > 0;
        };

        Controls.Add(root);
        AcceptButton = take;
        CancelButton = cancel;
        HandleCreated += (_, _) => UiTheme.ApplyTitleBar(this, darkMode);
        Shown += (_, _) => BeginInvoke(() => WindowActivation.ShowAndActivate(this));
        UiTheme.Apply(this, darkMode);
    }

    public IReadOnlyList<string> SelectedKeys => _entries.CheckedIndices
        .Cast<int>()
        .Select(index => _models[index].Key)
        .ToList();

    private void SetAllEntriesChecked(bool isChecked)
    {
        for (var i = 0; i < _entries.Items.Count; i++)
            _entries.SetItemChecked(i, isChecked);
    }
}
