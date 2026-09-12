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
        Assert.Equal(UiTheme.Accent, FindControls<Button>(form).Single(button => button.Name == "PageTabGeneral").BackColor);
        form.Show();
        Application.DoEvents();
        Assert.Equal(UiTheme.DarkBackground, form.BackColor);
        Assert.True(form.ClientSize.Width <= 720);
        Assert.True(form.ClientSize.Height <= 560);
        Assert.IsAssignableFrom<SmoothDpiForm>(form);
        Assert.Equal(FormBorderStyle.FixedSingle, form.FormBorderStyle);
        Assert.False(form.MaximizeBox);
        Assert.DoesNotContain(FindControls<ScrollableControl>(form), control => control.AutoScroll);
        Assert.Empty(FindControls<TabControl>(form));
        var pageButtons = FindControls<Button>(form)
            .Where(button => button.Name.StartsWith("PageTab", StringComparison.Ordinal))
            .Select(button => button.Text);
        Assert.Equal(["General", "Connections"], pageButtons);
        Assert.DoesNotContain(FindControls<Button>(form), button => button.Text is "Save" or "Cancel");
        Assert.DoesNotContain(FindControls<CheckBox>(form), checkBox => checkBox.Text == "Available updates");
        var folderList = Assert.Single(FindControls<ListBox>(form));
        var addFolder = FindControls<Button>(form).Single(button => button.Text == "Add folder");
        var folderLayout = Assert.IsType<TableLayoutPanel>(folderList.Parent);
        var folderButtons = Assert.IsType<FlowLayoutPanel>(addFolder.Parent);
        Assert.Same(folderLayout, folderButtons.Parent);
        Assert.Equal(0, folderLayout.GetRow(folderList));
        Assert.Equal(2, folderLayout.GetRow(folderButtons));
        AssertPagesContainContent(form, "before scaling");
        form.ShowAvailableUpdate(new Version(1, 2, 3), "https://github.com/example/ModRelay/releases/tag/v1.2.3");
        AssertPagesContainContent(form, "with an available update");

        form.Scale(new SizeF(1.5f, 1.5f));
        form.PerformLayout();
        Assert.DoesNotContain(FindControls<ScrollableControl>(form), control => control.AutoScroll);
        AssertPagesContainContent(form, "after 150% scaling");
    }

    private static void AssertPagesContainContent(SettingsForm form, string phase)
    {
        foreach (var button in FindControls<Button>(form)
                     .Where(button => button.Name.StartsWith("PageTab", StringComparison.Ordinal)))
        {
            button.PerformClick();
            form.PerformLayout();
            Application.DoEvents();
            AssertCardsContainContent(form, $"{button.Text} {phase}");
        }
    }

    private static void AssertCardsContainContent(SettingsForm form, string phase)
    {
        var cards = FindControls<Panel>(form)
            .Where(panel => panel.Visible && panel.Tag is TableLayoutPanel)
            .ToList();
        Assert.Equal(2, cards.Count);
        var page = Assert.IsType<Panel>(cards[0].Parent?.Parent);
        var contentBottom = cards.Max(card => card.Bottom);
        Assert.True(
            page.ClientSize.Height >= contentBottom,
            $"Visible page {phase} was {page.ClientSize.Height}px high but its cards need {contentBottom}px.");
        Assert.All(
            cards,
            card =>
            {
                var content = Assert.IsType<TableLayoutPanel>(card.Tag);
                Assert.True(
                    card.ClientSize.Height >= content.Bottom + card.Padding.Bottom,
                    $"Card {phase} was {card.ClientSize.Height}px high but its content needs {content.Bottom + card.Padding.Bottom}px.");
                foreach (var child in FindControls<Control>(card).Where(control => control.Visible))
                {
                    var parent = child.Parent!;
                    Assert.True(parent.ClientRectangle.Contains(child.Bounds),
                        $"{child.GetType().Name} '{child.Text}' {phase}: bounds {child.Bounds} exceed parent {parent.ClientRectangle}.");
                }
            });
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
    public void Header_ShowsSemanticVersion()
    {
        using var form = new SettingsForm(new AppConfig());

        Assert.Contains(FindControls<Label>(form), label =>
            label.Text.StartsWith("ModRelay v", StringComparison.Ordinal) &&
            label.Text.Count(character => character == '.') >= 2);
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
            .Single(checkBox => checkBox.Text == "Notify me about problems");

        notifications.Checked = false;

        Assert.NotNull(saved);
        Assert.False(saved.ShowErrorNotifications);
    }

    [Fact]
    public void FailedSave_RestoresTheCommittedValuesWithoutSavingAgain()
    {
        using var temp = new TestDirectory();
        var committed = new AppConfig { WatchFolders = [temp.Path], DarkMode = true };
        using var form = new SettingsForm(committed);
        var saves = 0;
        form.ConfigChanged += _ =>
        {
            saves++;
            form.RestoreConfig(committed);
        };
        var darkMode = FindControls<CheckBox>(form).Single(toggle => toggle.Text == "Use dark mode");

        darkMode.Checked = false;

        Assert.Equal(1, saves);
        Assert.True(darkMode.Checked);
        Assert.True(form.ResultConfig.DarkMode);
        Assert.Equal(UiTheme.DarkBackground, form.BackColor);
        Assert.Equal([temp.Path], form.ResultConfig.WatchFolders);
        Assert.StartsWith(temp.Path, Assert.Single(FindControls<ListBox>(form)).Items[0]!.ToString());
    }

    [Fact]
    public void ClosingSettings_SavesAPendingTexToolsPath()
    {
        using var form = new SettingsForm(new AppConfig());
        form.Show();
        AppConfig? saved = null;
        form.ConfigChanged += config => saved = config;
        FindControls<TextBox>(form).Single().Text = @"C:\TexTools\ConsoleTools.exe";

        form.Close();

        Assert.NotNull(saved);
        Assert.Equal(@"C:\TexTools\ConsoleTools.exe", saved.TexToolsConsolePath);
    }

    [Fact]
    public void WatcherProblems_AppearBesideTheFolderWithoutChangingTheSavedPath()
    {
        using var temp = new TestDirectory();
        var missing = Path.Combine(temp.Path, "missing");
        using var form = new SettingsForm(new AppConfig { WatchFolders = [temp.Path, missing] });
        AppConfig? saved = null;
        form.ConfigChanged += config => saved = config;

        form.UpdateWatchStatus([new(temp.Path, true, null), new(missing, false, "Folder does not exist or is unavailable.")], false);
        var list = FindControls<ListBox>(form).Single();
        Assert.Contains("[Watching]", list.Items[0]!.ToString());
        Assert.Contains("[Not watching]", list.Items[1]!.ToString());
        list.SelectedIndex = 1;
        Assert.Contains(FindControls<Label>(form), label => label.Text == "Folder does not exist or is unavailable.");
        Assert.Null(saved);

        FindControls<CheckBox>(form).Single(toggle => toggle.Text == "Use dark mode").Checked = false;
        Assert.Equal([temp.Path, missing], saved!.WatchFolders);
        form.UpdateWatchStatus([new(temp.Path, true, null), new(missing, true, null)], true);
        Assert.All(list.Items.Cast<object>(), item => Assert.Contains("[Paused]", item.ToString()));
        form.UpdateWatchStatus([new(temp.Path, true, "Some downloads could not be checked."), new(missing, true, null)], false);
        list.SelectedIndex = -1;
        Assert.Contains("[Check folder]", list.Items[0]!.ToString());
        Assert.Contains(FindControls<Label>(form), label => label.Text.Contains("1 folder(s) need attention", StringComparison.Ordinal));
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
