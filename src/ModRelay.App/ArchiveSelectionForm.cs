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
            Text = "Pre-DT is inferred from the package name. DT / unknown means no legacy marker was found.",
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
            var generation = entry.LooksPreDawntrail ? "PRE-DT" : "DT / UNKNOWN";
            _entries.Items.Add($"[{generation}]  {entry.FileName}", true);
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
        var cancel = UiTheme.Button("Skip");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(take);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons);

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
}
