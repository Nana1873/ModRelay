using System.Text.RegularExpressions;

namespace ModRelay.Core;

public static partial class ModFileTypes
{
    /// <summary>Files Penumbra can consume, directly or after conversion.</summary>
    public static readonly string[] Mod = [".ttmp", ".ttmp2", ".pmp", ".pcp"];

    /// <summary>Containers we look inside.</summary>
    public static readonly string[] Archive = [".zip", ".7z", ".rar"];

    public static bool IsModFile(string path) =>
        Mod.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsArchive(string path) =>
        Archive.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Heuristic for "this mod predates Dawntrail", based on how mod authors name their files.
    /// Only ever used to decide what to <em>offer for conversion</em> - never to skip a file.
    /// </summary>
    public static bool LooksPreDawntrail(string fileName) =>
        PreDtPattern().IsMatch(fileName) ||
        fileName.Contains("Endwalker", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"pre[-_ ]?dt|pre[-_ ]?dawntrail|(?:^|[^a-z0-9])ew(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PreDtPattern();
}
