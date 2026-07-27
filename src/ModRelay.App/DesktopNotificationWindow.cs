using System.Runtime.InteropServices;
using ModRelay.Core;

namespace ModRelay.App;

internal sealed class DesktopNotificationWindow : Form
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private readonly System.Windows.Forms.Timer _closeTimer;
    private readonly double _visibleOpacity = 0.97;

    public DesktopNotificationWindow(string title, string message, bool isError, Screen targetScreen)
    {
        TargetScreen = targetScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(390, 104);
        BackColor = Color.FromArgb(20, 22, 31);
        ForeColor = Color.White;
        Font = UiTheme.Font();
        Opacity = _visibleOpacity;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);

        var accent = new Panel
        {
            Dock = DockStyle.Left,
            Width = 5,
            BackColor = isError ? Color.FromArgb(218, 74, 74) : UiTheme.Accent
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16, 12, 16, 11),
            BackColor = BackColor
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label
        {
            Text = title,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Height = 27,
            ForeColor = Color.White,
            Font = UiTheme.Font(10, FontStyle.Bold),
            Margin = Padding.Empty
        });
        content.Controls.Add(new Label
        {
            Text = message,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(205, 210, 225),
            Font = UiTheme.Font(9),
            Margin = new Padding(0, 3, 0, 0)
        });
        Controls.Add(content);
        Controls.Add(accent);
        MakeDismissible(this);

        _closeTimer = new System.Windows.Forms.Timer { Interval = isError ? 8500 : 6000 };
        _closeTimer.Tick += (_, _) => Close();
        Shown += (_, _) => _closeTimer.Start();
        FormClosed += (_, _) => _closeTimer.Dispose();
    }

    public Screen TargetScreen { get; }
    internal bool DoesNotActivate => ShowWithoutActivation;
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public void PrepareForDisplay(Point provisionalLocation)
    {
        // Creating the native handle can change Width and Height when the target
        // monitor has a different DPI. Keep the window transparent until the
        // manager has positioned it again using that final scaled size.
        Opacity = 0;
        Location = provisionalLocation;
        Show();
    }

    public void Reveal()
    {
        Opacity = _visibleOpacity;
        SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
        var workArea = TargetScreen.WorkingArea;
        Log.Info(
            $"Desktop notification bounds: {Bounds}; target work area: {workArea}; " +
            $"fully inside: {workArea.Contains(Bounds)}; dpi: {DeviceDpi}.");
    }

    public void MoveTo(Point location)
    {
        Location = location;
        if (IsHandleCreated)
            SetWindowPos(Handle, HwndTopmost, location.X, location.Y, Width, Height, SwpNoActivate);
    }

    private static void MakeDismissible(Control control)
    {
        control.Cursor = Cursors.Hand;
        control.Click += (_, _) => control.FindForm()?.Close();
        foreach (Control child in control.Controls)
            MakeDismissible(child);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

internal sealed class DesktopNotificationManager
{
    private const int ScreenMargin = 16;
    private const int Gap = 9;
    private const int MaximumVisible = 3;
    private readonly List<DesktopNotificationWindow> _active = [];

    public void Show(string title, string message, bool isError)
    {
        var screen = ForegroundScreen();
        _active.RemoveAll(notification => notification.IsDisposed);
        while (_active.Count(item => item.TargetScreen.DeviceName == screen.DeviceName) >= MaximumVisible)
        {
            var oldest = _active.First(item => item.TargetScreen.DeviceName == screen.DeviceName);
            _active.Remove(oldest);
            oldest.Close();
        }

        var notification = new DesktopNotificationWindow(title, message, isError, screen);
        notification.FormClosed += (_, _) =>
        {
            _active.Remove(notification);
            Arrange(screen);
        };
        var provisionalLocation = new Point(
            screen.WorkingArea.Right - ScreenMargin - notification.Width,
            screen.WorkingArea.Bottom - ScreenMargin - notification.Height);
        notification.PrepareForDisplay(provisionalLocation);
        _active.Add(notification);
        Arrange(screen);
        notification.Reveal();
    }

    private void Arrange(Screen screen)
    {
        var y = screen.WorkingArea.Bottom - ScreenMargin;
        foreach (var notification in _active
                     .Where(item => item.TargetScreen.DeviceName == screen.DeviceName)
                     .Reverse())
        {
            y -= notification.Height;
            var location = new Point(screen.WorkingArea.Right - ScreenMargin - notification.Width, y);
            notification.MoveTo(location);
            y -= Gap;
        }
    }

    private static Screen ForegroundScreen()
    {
        var foreground = GetForegroundWindow();
        return foreground == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(foreground);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
