using System.Runtime.InteropServices;

namespace ModRelay.App;

internal static class WindowActivation
{
    private const int SwRestore = 9;

    public static void ShowAndActivate(Form form)
    {
        if (!form.Visible)
            form.Show();

        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;

        // The process can be launched with STARTF_USESHOWWINDOW/SW_HIDE by a
        // launcher. WinForms then considers the form visible even though the
        // desktop window is still hidden. An explicit second ShowWindow call
        // overrides that inherited startup state.
        ShowWindow(form.Handle, SwRestore);
        SetForegroundWindow(form.Handle);
        form.Activate();
        form.BringToFront();
    }

    public static Screen ForegroundScreen()
    {
        var foreground = GetForegroundWindow();
        return foreground == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(foreground);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
