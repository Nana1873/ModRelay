using ModRelay.Core;

namespace ModRelay.App;

internal sealed class UpgradeConfirmationForm : SmoothDpiForm
{
    public UpgradeConfirmationForm(string fileName, UpgradeResult result, bool darkMode)
    {
        Text = "Dawntrail upgrade failed";
        ClientSize = new Size(520, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.Font();
        Icon = AppIcon.Current;

        var reason = result.Status == UpgradeStatus.ToolMissing
            ? "TexTools ConsoleTools.exe is not configured."
            : $"TexTools could not upgrade the mod (exit code {result.ExitCode}).";
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(new Label
        {
            Text = $"{reason}\n\n{fileName}\n\nSend the unchanged original to Penumbra?",
            Dock = DockStyle.Fill,
            AutoEllipsis = true
        }, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var skip = UiTheme.Button("Skip this mod", primary: true);
        skip.DialogResult = DialogResult.No;
        var send = UiTheme.Button("Send original");
        send.DialogResult = DialogResult.Yes;
        buttons.Controls.Add(skip);
        buttons.Controls.Add(send);
        body.Controls.Add(buttons, 0, 1);
        Controls.Add(body);
        AcceptButton = skip;
        CancelButton = skip;
        HandleCreated += (_, _) => UiTheme.ApplyTitleBar(this, darkMode);
        UiTheme.Apply(this, darkMode);
    }
}
