using ModRelay.App;
using ModRelay.Core;
using System.Drawing;
using System.Windows.Forms;

namespace ModRelay.Tests;

public sealed class SettingsLayoutTests
{
    [Fact]
    public void SettingsUseCompactPagesWithoutAMainScrollbar()
    {
        using var temp = new TestDirectory();
        using var form = new SettingsForm(new AppConfig { WatchFolders = [temp.Path] });
        Assert.Equal(UiTheme.DarkBackground, form.BackColor);
        Assert.True(form.ClientSize.Width <= 720);
        Assert.True(form.ClientSize.Height <= 540);
        Assert.IsAssignableFrom<SmoothDpiForm>(form);
        Assert.Equal(FormBorderStyle.FixedSingle, form.FormBorderStyle);
        Assert.False(form.MaximizeBox);
        Assert.DoesNotContain(FindControls<ScrollableControl>(form), control => control.AutoScroll);
        Assert.Empty(FindControls<TabControl>(form));
        var pageButtons = FindControls<Button>(form)
            .Where(button => button.Name.StartsWith("PageTab", StringComparison.Ordinal))
            .Select(button => button.Text);
        Assert.Equal(["General", "Connections", "Advanced"], pageButtons);
        Assert.DoesNotContain(FindControls<Button>(form), button => button.Text is "Save" or "Cancel");
        Assert.DoesNotContain(FindControls<CheckBox>(form), checkBox => checkBox.Text == "Available updates");
        var folderList = Assert.Single(FindControls<ListBox>(form));
        var addFolder = FindControls<Button>(form).Single(button => button.Text == "Add folder");
        var folderLayout = Assert.IsType<TableLayoutPanel>(folderList.Parent);
        var folderButtons = Assert.IsType<FlowLayoutPanel>(addFolder.Parent);
        Assert.Same(folderLayout, folderButtons.Parent);
        Assert.Equal(0, folderLayout.GetRow(folderList));
        Assert.Equal(1, folderLayout.GetRow(folderButtons));

        form.Scale(new SizeF(1.5f, 1.5f));
        Assert.DoesNotContain(FindControls<ScrollableControl>(form), control => control.AutoScroll);
    }

    [Fact]
    public void AvailableUpdate_IsShownInsideSettings()
    {
        using var form = new SettingsForm(new AppConfig());

        form.ShowAvailableUpdate(new Version(1, 2, 3), "https://github.com/example/ModRelay/releases/tag/v1.2.3");

        Assert.True(form.HasAvailableUpdate);
        Assert.Contains(FindControls<Label>(form), label => label.Text.Contains("1.2.3", StringComparison.Ordinal));
        Assert.Contains(FindControls<Button>(form), button => button.Text == "View update");
    }

    [Fact]
    public void LightModeCanBeSelectedExplicitly()
    {
        using var temp = new TestDirectory();
        using var form = new SettingsForm(new AppConfig { WatchFolders = [temp.Path], DarkMode = false });

        Assert.Equal(UiTheme.Background, form.BackColor);
    }

    [Fact]
    public void ChangedToggle_IsPublishedImmediatelyForAutoSave()
    {
        using var temp = new TestDirectory();
        using var form = new SettingsForm(new AppConfig { WatchFolders = [temp.Path] });
        AppConfig? saved = null;
        form.ConfigChanged += config => saved = config;
        var notifications = FindControls<CheckBox>(form)
            .Single(checkBox => checkBox.Text == "Successful imports");

        notifications.Checked = false;

        Assert.NotNull(saved);
        Assert.False(saved.ShowNotifications);
    }

    private static IEnumerable<T> FindControls<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match)
                yield return match;

            foreach (var nested in FindControls<T>(child))
                yield return nested;
        }
    }

}
