using System.Runtime.InteropServices;

namespace ModRelay.App;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(245, 247, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color Header = Color.FromArgb(28, 31, 45);
    public static readonly Color Accent = Color.FromArgb(111, 78, 235);
    public static readonly Color Text = Color.FromArgb(31, 35, 48);
    public static readonly Color Muted = Color.FromArgb(99, 107, 125);
    public static readonly Color Border = Color.FromArgb(220, 224, 234);
    public static readonly Color Success = Color.FromArgb(34, 139, 94);
    public static readonly Color DarkBackground = Color.FromArgb(16, 18, 26);
    public static readonly Color DarkSurface = Color.FromArgb(27, 30, 42);
    public static readonly Color DarkInput = Color.FromArgb(36, 40, 56);
    public static readonly Color DarkHeader = Color.FromArgb(12, 14, 21);
    public static readonly Color DarkText = Color.FromArgb(238, 240, 246);
    public static readonly Color DarkMuted = Color.FromArgb(174, 181, 197);
    public static readonly Color DarkBorder = Color.FromArgb(58, 63, 82);

    public static Font Font(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(96, 32),
            Padding = new Padding(10, 1, 10, 1),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Color.White : Text,
            Font = Font(9, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.Tag = primary ? "primary" : "secondary";
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        return button;
    }

    public static Panel Card()
    {
        var panel = new Panel
        {
            BackColor = Surface,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 8),
            AutoSize = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        return panel;
    }

    public static void Apply(Form form, bool dark)
    {
        form.BackColor = dark ? DarkBackground : Background;
        ApplyRecursive(form, dark, inHeader: false);
        ApplyTitleBar(form, dark);
    }

    public static void Apply(ContextMenuStrip menu, bool dark)
    {
        var background = dark ? DarkSurface : Surface;
        var foreground = dark ? DarkText : Text;
        menu.BackColor = background;
        menu.ForeColor = foreground;
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(dark));
        ApplyMenuItems(menu.Items, background, foreground);
    }

    private static void ApplyMenuItems(ToolStripItemCollection items, Color background, Color foreground)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            if (item is not ToolStripMenuItem menuItem || menuItem.DropDownItems.Count == 0)
                continue;

            menuItem.DropDown.BackColor = background;
            menuItem.DropDown.ForeColor = foreground;
            ApplyMenuItems(menuItem.DropDownItems, background, foreground);
        }
    }

    private static void ApplyRecursive(Control parent, bool dark, bool inHeader)
    {
        foreach (Control control in parent.Controls)
        {
            var header = inHeader || Equals(control.Tag, "header");
            ApplyControl(control, dark, header);
            ApplyRecursive(control, dark, header);
        }
    }

    private static void ApplyControl(Control control, bool dark, bool inHeader)
    {
        var background = dark ? DarkBackground : Background;
        var surface = dark ? DarkSurface : Surface;
        var input = dark ? DarkInput : Color.White;
        var text = dark ? DarkText : Text;
        var muted = dark ? DarkMuted : Muted;
        var border = dark ? DarkBorder : Border;

        switch (control)
        {
            case Button button:
                var primary = Equals(button.Tag, "primary");
                button.BackColor = primary ? Accent : surface;
                button.ForeColor = primary ? Color.White : text;
                button.FlatAppearance.BorderColor = primary ? Accent : border;
                break;

            case TextBox or ListBox or NumericUpDown:
                control.BackColor = input;
                control.ForeColor = text;
                break;

            case LinkLabel link:
                link.BackColor = surface;
                link.ForeColor = muted;
                link.LinkColor = dark ? Color.FromArgb(157, 132, 255) : Accent;
                link.ActiveLinkColor = dark ? Color.FromArgb(190, 175, 255) : Accent;
                break;

            case CheckBox checkBox:
                checkBox.BackColor = surface;
                checkBox.ForeColor = Equals(checkBox.Tag, "danger")
                    ? Color.FromArgb(220, 120, 105)
                    : text;
                break;

            case Label label:
                label.BackColor = Color.Transparent;
                if (inHeader)
                    label.ForeColor = label.Font.Bold ? Color.White : Color.FromArgb(190, 196, 214);
                else if (label.ForeColor == Muted || label.ForeColor == DarkMuted)
                    label.ForeColor = muted;
                else
                    label.ForeColor = text;
                break;

            case Panel panel when panel.Tag is TableLayoutPanel:
                panel.BackColor = surface;
                break;

            case TableLayoutPanel table when Equals(table.Tag, "header"):
                table.BackColor = dark ? DarkHeader : Header;
                break;

            case TableLayoutPanel table when Equals(table.Tag, "update-banner"):
                table.BackColor = dark ? Color.FromArgb(38, 32, 70) : Color.FromArgb(235, 230, 255);
                break;

            case FlowLayoutPanel flow when flow.Dock == DockStyle.Fill && flow.AutoSize:
                flow.BackColor = surface;
                break;

            default:
                control.BackColor = inHeader
                    ? (dark ? DarkHeader : Header)
                    : control.Parent?.BackColor == surface ? surface : background;
                break;
        }
    }

    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated || !OperatingSystem.IsWindows())
            return;

        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class MenuColors(bool dark) : ProfessionalColorTable
    {
        private Color Background => dark ? DarkSurface : Surface;
        private Color Highlight => dark ? DarkInput : Background;
        private Color Edge => dark ? DarkBorder : Border;

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemBorder => Edge;
        public override Color MenuBorder => Edge;
        public override Color SeparatorDark => Edge;
        public override Color SeparatorLight => Edge;
    }
}
