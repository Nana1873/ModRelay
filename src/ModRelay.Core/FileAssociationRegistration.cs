using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ModRelay.Core;

[SupportedOSPlatform("windows")]
public static class FileAssociationRegistration
{
    private const string ProgId = "ModRelay.Mod";
    private static readonly string[] Extensions = [".ttmp", ".ttmp2", ".pmp", ".pcp"];

    public static void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled)
        {
            using (var command = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\{ProgId}\shell\open\command", writable: true))
            {
                command?.SetValue(string.Empty, $"\"{executablePath}\" \"%1\"");
            }

            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}", writable: true))
            {
                progId?.SetValue(string.Empty, "FFXIV mod package");
            }

            foreach (var extension in Extensions)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}", writable: true);
                key?.SetValue(string.Empty, ProgId);
            }
        }
        else
        {
            foreach (var extension in Extensions)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false);
                var current = key?.GetValue(string.Empty)?.ToString();
                if (string.Equals(current, ProgId, StringComparison.Ordinal))
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}", throwOnMissingSubKey: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        }
    }
}
