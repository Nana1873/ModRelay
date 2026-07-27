using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ModRelay.Core;

public enum UpgradeStatus
{
    /// <summary>A converted file was produced.</summary>
    Upgraded,

    /// <summary>ConsoleTools ran fine but had nothing to do - the mod is already Dawntrail-ready.</summary>
    NotNeeded,

    /// <summary>ConsoleTools.exe is not configured or not where the config says it is.</summary>
    ToolMissing,

    /// <summary>ConsoleTools ran and failed. Details are in <see cref="UpgradeResult.Output"/>.</summary>
    Failed
}

public sealed record UpgradeResult(UpgradeStatus Status, string? OutputPath, int ExitCode, string Output);

/// <summary>
/// Wraps TexTools' <c>ConsoleTools.exe /upgrade "&lt;source&gt;" "&lt;target&gt;"</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TexToolsUpgrader
{
    private const string UninstallKey =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\FFXIV_TexTools";

    public const string DownloadUrl = "https://github.com/TexTools/FFXIV_TexTools_UI/releases";

    /// <summary>
    /// Registry first, then the two standard install locations. Deliberately no
    /// full drive scan - it takes minutes and the settings window has a Browse button.
    /// </summary>
    public static string? Locate()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate))
            {
                Log.Info($"Found ConsoleTools.exe at {candidate}");
                return candidate;
            }
        }

        Log.Info("ConsoleTools.exe not found in registry or standard paths.");
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var fromRegistry = RegistryInstallLocation();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            var root = fromRegistry.Trim('"');
            yield return Path.Combine(root, "ConsoleTools.exe");
            yield return Path.Combine(root, "FFXIV_TexTools", "ConsoleTools.exe");
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "FFXIV TexTools", "FFXIV_TexTools", "ConsoleTools.exe");
        }
    }

    private static string? RegistryInstallLocation()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation")?.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the TexTools registry key.", ex);
            return null;
        }
    }

    /// <summary>
    /// Converts <paramref name="sourcePath"/> to Dawntrail format.
    /// A failure is reported as a failure - the caller decides what happens next,
    /// rather than an un-upgraded mod quietly sliding into Penumbra.
    /// </summary>
    public async Task<UpgradeResult> UpgradeAsync(
        string consoleToolsPath,
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consoleToolsPath) || !File.Exists(consoleToolsPath))
        {
            Log.Warn($"TexTools ConsoleTools.exe not available at '{consoleToolsPath}'.");
            return new UpgradeResult(UpgradeStatus.ToolMissing, null, -1, string.Empty);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = consoleToolsPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(consoleToolsPath) ?? string.Empty
        };
        startInfo.ArgumentList.Add("/upgrade");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(targetPath);

        Log.Info($"Running: \"{consoleToolsPath}\" /upgrade \"{sourcePath}\" \"{targetPath}\"");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            // Read both pipes concurrently; reading them one after the other
            // deadlocks as soon as one of them fills up.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = string.Join(Environment.NewLine,
                new[] { await stdout, await stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            var produced = File.Exists(targetPath);

            if (process.ExitCode == 0 && produced)
            {
                Log.Info($"Upgrade succeeded: {targetPath}");
                return new UpgradeResult(UpgradeStatus.Upgraded, targetPath, 0, output);
            }

            if (process.ExitCode == 0)
            {
                Log.Info($"ConsoleTools reported nothing to upgrade for {sourcePath}.");
                return new UpgradeResult(UpgradeStatus.NotNeeded, null, 0, output);
            }

            Log.Error($"ConsoleTools exited with {process.ExitCode} for {sourcePath}. Output: {output}");

            // A failed run can still leave a half-written file behind.
            if (produced)
                TryDelete(targetPath);

            return new UpgradeResult(UpgradeStatus.Failed, null, process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not run ConsoleTools.exe for {sourcePath}.", ex);
            return new UpgradeResult(UpgradeStatus.Failed, null, -1, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not stop ConsoleTools.exe.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the incomplete conversion at {path}.", ex);
        }
    }
}
