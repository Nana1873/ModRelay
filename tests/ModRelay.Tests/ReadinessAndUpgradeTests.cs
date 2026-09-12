using ModRelay.Core;
using System.Diagnostics;
using System.Text;

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
    public void FileReadiness_IgnoresAnUnrelatedPartialFileWithTheSameStem()
    {
        using var temp = new TestDirectory();
        var file = temp.File("mod.zip");
        File.WriteAllText(file, "content");
        File.WriteAllText(temp.File("modpack.part"), "partial");

        Assert.False(FileReadiness.IsReady(file, -1, out var size));
        Assert.True(FileReadiness.IsReady(file, size, out _));
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

    [Fact]
    public async Task TexTools_CancellationStopsTheStartedSyntheticProcessAndRemovesOnlyItsOutput()
    {
        using var temp = new TestDirectory();
        var source = temp.File("Outfit (EW).ttmp2");
        var target = temp.File("Outfit (EW)_dt.ttmp2");
        var ready = temp.File("synthetic-upgrade-started.txt");
        var syntheticConsoleTools = temp.File("synthetic-console-tools.exe");
        File.WriteAllText(source, "original source");
        File.WriteAllText(syntheticConsoleTools, "test seam replaces this executable");
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedArguments = new List<string>();

        var upgrader = new TexToolsUpgrader(startInfo =>
        {
            receivedArguments.AddRange(startInfo.ArgumentList);
            startInfo.FileName = PowerShellPath;
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodePowerShell(
                $"Set-Content -LiteralPath {PowerShellLiteral(target)} -Value 'partial' -NoNewline; " +
                $"Set-Content -LiteralPath {PowerShellLiteral(ready)} -Value 'ready' -NoNewline; " +
                "Start-Sleep -Seconds 30"));
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => exited.TrySetResult();
            return process;
        });
        using var cancellation = new CancellationTokenSource();

        var upgrade = upgrader.UpgradeAsync(syntheticConsoleTools, source, target, cancellation.Token);
        await WaitForFileAsync(ready, upgrade);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upgrade);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["/upgrade", source, target], receivedArguments);
        Assert.Equal("original source", File.ReadAllText(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task TexTools_PreCanceledUpgradeDoesNotStartAProcessOrTouchAnExistingTarget()
    {
        using var temp = new TestDirectory();
        var source = temp.File("Outfit (EW).ttmp2");
        var target = temp.File("Outfit (EW)_dt.ttmp2");
        var syntheticConsoleTools = temp.File("synthetic-console-tools.exe");
        File.WriteAllText(source, "original source");
        File.WriteAllText(target, "preexisting output");
        File.WriteAllText(syntheticConsoleTools, "test seam replaces this executable");
        var processCreated = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var upgrader = new TexToolsUpgrader(_ =>
        {
            processCreated = true;
            throw new InvalidOperationException("A pre-canceled conversion must not create a process.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            upgrader.UpgradeAsync(syntheticConsoleTools, source, target, cancellation.Token));

        Assert.False(processCreated);
        Assert.Equal("original source", File.ReadAllText(source));
        Assert.Equal("preexisting output", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("Outfit Pre-DT.ttmp2")]
    [InlineData("Outfit Endwalker.pmp")]
    [InlineData("Outfit_EW.ttmp")]
    public void PreDawntrailNames_AreDetected(string name) =>
        Assert.True(ModFileTypes.LooksPreDawntrail(name));

    private static string PowerShellPath => Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static async Task WaitForFileAsync(string path, Task<UpgradeResult> upgrade)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(path))
        {
            if (upgrade.IsCompleted)
            {
                var result = await upgrade;
                throw new Xunit.Sdk.XunitException(
                    $"The synthetic upgrade exited before signalling readiness: {result.Status}, " +
                    $"exit code {result.ExitCode}, output: {result.Output}");
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The synthetic upgrade process did not start.");
            await Task.Delay(25);
        }
    }

    private static string EncodePowerShell(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string PowerShellLiteral(string path) => $"'{path.Replace("'", "''")}'";
}
