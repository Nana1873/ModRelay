using ModRelay.App;
using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class ArchiveUiTests
{
    [Fact]
    public void Selection_IsForegroundAndLabelsLikelyPreDtEntriesWithArchivePaths()
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
        Assert.Equal("new/Outfit.pmp", list.Items[1]!.ToString());
        Assert.Contains(FindControls<Label>(form), label => label.Text.Contains("inferred", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_DisambiguatesSameNameEntriesAndRequiresAnExplicitChoice()
    {
        var entries = new[]
        {
            new ArchiveEntryInfo("variants/a/Outfit.pmp", "Outfit.pmp", 10, false),
            new ArchiveEntryInfo("variants/b/Outfit.pmp", "Outfit.pmp", 20, false)
        };

        using var form = new ArchiveSelectionForm("bundle.zip", entries, darkMode: true);
        form.Show();
        Application.DoEvents();
        var list = Assert.Single(FindControls<CheckedListBox>(form));
        var extract = FindControls<Button>(form).Single(button => button.Text == "Extract selected");
        var selectAll = FindControls<Button>(form).Single(button => button.Text == "Select all");
        var clear = FindControls<Button>(form).Single(button => button.Text == "Clear");

        Assert.Equal(new[] { "variants/a/Outfit.pmp", "variants/b/Outfit.pmp" },
            list.Items.Cast<object>().Select(item => item.ToString()!));
        Assert.Empty(list.CheckedIndices.Cast<int>());
        Assert.False(extract.Enabled);

        list.SetItemChecked(1, true);
        Assert.True(extract.Enabled);
        Assert.Equal(["variants/b/Outfit.pmp"], form.SelectedKeys);

        selectAll.PerformClick();
        Assert.Equal(entries.Length, list.CheckedIndices.Count);
        Assert.Equal(entries.Select(entry => entry.Key), form.SelectedKeys);

        clear.PerformClick();
        Assert.Empty(list.CheckedIndices.Cast<int>());
        Assert.False(extract.Enabled);
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

    [Fact]
    public void Progress_CanCancelOnlyOnceWhileTheOperationStops()
    {
        using var form = new ArchiveProgressForm("bundle.zip", "Extracting…", true);
        form.Show();
        var requested = 0;
        form.CancelRequested += () => requested++;
        var cancel = FindControls<Button>(form).Single();
        cancel.PerformClick();
        cancel.PerformClick();
        Assert.Equal(1, requested);
        Assert.False(cancel.Enabled);
        Assert.Equal("Cancelling…", cancel.Text);
    }

    [Fact]
    public void UpgradeConfirmation_DefaultsToSkippingTheOriginal()
    {
        using var form = new UpgradeConfirmationForm("old.ttmp2", new UpgradeResult(UpgradeStatus.ToolMissing, null, -1, ""), true);
        Assert.Equal(DialogResult.No, Assert.IsType<Button>(form.AcceptButton).DialogResult);
        Assert.Equal(DialogResult.No, Assert.IsType<Button>(form.CancelButton).DialogResult);
        Assert.Contains(FindControls<Button>(form), button => button.DialogResult == DialogResult.Yes);
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
