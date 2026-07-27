using System.Runtime.InteropServices;

namespace ModRelay.App;

internal sealed class ArchiveProgressForm : SmoothDpiForm
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private readonly Label _message;

    public ArchiveProgressForm(string archiveName, string message, bool darkMode)
    {
        Text = "Preparing archive";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(500, 142);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = UiTheme.Background;
        Font = UiTheme.Font();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Icon = AppIcon.Current;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "Preparing archive",
            AutoSize = true,
            Font = UiTheme.Font(12, FontStyle.Bold),
            ForeColor = UiTheme.Text,
            Margin = new Padding(0, 0, 0, 3)
        });
        root.Controls.Add(new Label
        {
            Text = archiveName,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0, 0, 0, 7)
        });
        _message = new Label
        {
            Text = message,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Text,
            Margin = new Padding(0, 0, 0, 7)
        };
        root.Controls.Add(_message);
        root.Controls.Add(new ProgressBar
        {
            Dock = DockStyle.Fill,
            Height = 7,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24
        });

        Controls.Add(root);
        HandleCreated += (_, _) => UiTheme.ApplyTitleBar(this, darkMode);
        UiTheme.Apply(this, darkMode);
    }

    protected override bool ShowWithoutActivation => true;
    internal bool DoesNotActivate => ShowWithoutActivation;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public void ShowOn(Screen screen)
    {
        Opacity = 0;
        Location = new Point(screen.WorkingArea.Left, screen.WorkingArea.Top);
        Show();

        // Showing creates the native handle and applies the target monitor's DPI.
        // Center only afterwards so mixed-DPI monitors cannot clip the window.
        Location = new Point(
            screen.WorkingArea.Left + (screen.WorkingArea.Width - Width) / 2,
            screen.WorkingArea.Top + (screen.WorkingArea.Height - Height) / 2);
        Opacity = 0.98;
    }

    public void UpdateMessage(string message) => _message.Text = message;
}
