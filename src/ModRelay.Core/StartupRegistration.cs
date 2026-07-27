using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ModRelay.Core;

[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");

        if (enabled)
            key.SetValue(AppPaths.AppName, $"\"{executablePath}\"");
        else
            key.DeleteValue(AppPaths.AppName, throwOnMissingValue: false);

    }
}
