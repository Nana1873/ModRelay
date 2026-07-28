using ModRelay.Core;

namespace ModRelay.Tests;

public sealed class ReadinessAndUpgradeTests
{
    [Fact]
    public void FileReadiness_RequiresStableSizeAndNoPartialSibling()
    {
        using var temp = new TestDirectory();
        var file = temp.File("mod.ttmp2");
        File.WriteAllText(file, "content");

        Assert.False(FileReadiness.IsReady(file, -1, out var size));
        Assert.True(FileReadiness.IsReady(file, size, out _));

        File.WriteAllText(file + ".crdownload", "partial");
        Assert.False(FileReadiness.IsReady(file, size, out _));
    }

    [Fact]
    public async Task TexTools_MissingExecutable_IsAnExplicitFailure()
    {
        using var temp = new TestDirectory();
        var source = temp.File("Outfit (EW).ttmp2");
        File.WriteAllText(source, "mod");

        var result = await new TexToolsUpgrader().UpgradeAsync(
            temp.File("missing.exe"), source, temp.File("out_dt.ttmp2"));

        Assert.Equal(UpgradeStatus.ToolMissing, result.Status);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public async Task TexTools_ExistingTarget_IsNeverReusedOrDeleted()
    {
        using var temp = new TestDirectory();
        var source = temp.File("Outfit (EW).ttmp2");
        var target = temp.File("Outfit (EW)_dt.ttmp2");
        File.WriteAllText(source, "source");
        File.WriteAllText(target, "existing output");
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")!;

        var result = await new TexToolsUpgrader().UpgradeAsync(commandInterpreter, source, target);

        Assert.Equal(UpgradeStatus.Failed, result.Status);
        Assert.Equal("existing output", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("Outfit Pre-DT.ttmp2")]
    [InlineData("Outfit Endwalker.pmp")]
    [InlineData("Outfit_EW.ttmp")]
    public void PreDawntrailNames_AreDetected(string name) =>
        Assert.True(ModFileTypes.LooksPreDawntrail(name));
}
