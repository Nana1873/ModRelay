using ModRelay.App;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class ArchiveUiTests
{
    [Fact]
    public void Selection_IsForegroundAndOnlyLabelsLikelyPreDtEntries()
    {
        var entries = new[]
        {
            new ArchiveEntryInfo("old/Outfit Pre-DT.ttmp2", "Outfit Pre-DT.ttmp2", 10, true),
            new ArchiveEntryInfo("new/Outfit.pmp", "Outfit.pmp", 20, false)
        };

        using var form = new ArchiveSelectionForm("bundle.zip", entries, darkMode: true);
        var list = Assert.Single(FindControls<CheckedListBox>(form));

        Assert.True(form.TopMost);
        Assert.StartsWith("[PRE-DT?]", list.Items[0]!.ToString());
        Assert.Equal("Outfit.pmp", list.Items[1]!.ToString());
        Assert.Contains(FindControls<Label>(form), label => label.Text.Contains("inferred", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_ContextMenuCanSelectNoneAndAll()
    {
        var entries = new[]
        {
            new ArchiveEntryInfo("first.pmp", "first.pmp", 10, false),
            new ArchiveEntryInfo("second.pmp", "second.pmp", 20, false)
        };

        using var form = new ArchiveSelectionForm("bundle.zip", entries, darkMode: true);
        var list = Assert.Single(FindControls<CheckedListBox>(form));
        var menu = Assert.IsType<ContextMenuStrip>(list.ContextMenuStrip);
        Assert.Equal(new[] { "Select all", "Select none" }, menu.Items.Cast<ToolStripItem>().Select(item => item.Text));

        menu.Items[1].PerformClick();
        Assert.Empty(list.CheckedIndices.Cast<int>());

        menu.Items[0].PerformClick();
        Assert.Equal(entries.Length, list.CheckedIndices.Count);
    }

    [Fact]
    public void Progress_IsTopmostWithoutTakingFocus()
    {
        using var form = new ArchiveProgressForm("bundle.zip", "Scanning…", darkMode: true);

        Assert.True(form.TopMost);
        Assert.False(form.ShowInTaskbar);
        Assert.True(form.DoesNotActivate);
        Assert.Single(FindControls<ProgressBar>(form));
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
