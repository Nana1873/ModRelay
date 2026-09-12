using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ModRelay.Core;

[SupportedOSPlatform("windows")]
public static class FileAssociationRegistration
{
    private const string ProgId = "ModRelay.Mod";
    private const string PreviousAssociationPrefix = "PreviousAssociation:";
    private const uint AssociationChanged = 0x08000000;
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
                var current = key?.GetValue(string.Empty)?.ToString();
                if (!string.Equals(current, ProgId, StringComparison.Ordinal))
                {
                    using var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}", writable: true);
                    progId?.SetValue(PreviousAssociationPrefix + extension, current ?? string.Empty);
                }
                key?.SetValue(string.Empty, ProgId);

                using var openWith = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
        }
        else
        {
            foreach (var extension in Extensions)
            {
                using (var openWith = Registry.CurrentUser.OpenSubKey(
                           $@"Software\Classes\{extension}\OpenWithProgids", writable: true))
                {
                    openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                }

                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false);
                var current = key?.GetValue(string.Empty)?.ToString();
                if (string.Equals(current, ProgId, StringComparison.Ordinal))
                {
                    using var writableKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: true);
                    using var progId = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}", writable: false);
                    var previous = progId?.GetValue(PreviousAssociationPrefix + extension)?.ToString();
                    if (string.IsNullOrEmpty(previous))
                        writableKey?.DeleteValue(string.Empty, throwOnMissingValue: false);
                    else
                        writableKey?.SetValue(string.Empty, previous);
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        }

        SHChangeNotify(AssociationChanged, 0, 0, 0);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
