namespace ModRelay.App;

/// <summary>
/// Batches WinForms' nested layout work during a per-monitor DPI transition and
/// double-buffers repainting to reduce the hitch and flicker between monitors.
/// </summary>
internal class SmoothDpiForm : Form
{
    private const int WmDpiChanged = 0x02E0;

    protected SmoothDpiForm()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg != WmDpiChanged)
        {
            base.WndProc(ref message);
            return;
        }

        SuspendLayoutTree(this);
        try
        {
            base.WndProc(ref message);
        }
        finally
        {
            ResumeLayoutTree(this);
            PerformLayout();
            Invalidate(true);
        }
    }

    private static void SuspendLayoutTree(Control control)
    {
        control.SuspendLayout();
        foreach (Control child in control.Controls)
            SuspendLayoutTree(child);
    }

    private static void ResumeLayoutTree(Control control)
    {
        foreach (Control child in control.Controls)
            ResumeLayoutTree(child);
        control.ResumeLayout(performLayout: false);
    }
}
