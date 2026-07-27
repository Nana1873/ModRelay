using System.Runtime.InteropServices;
using Microsoft.Win32;
using ModRelay.Core;

namespace ModRelay.App;

internal static class WindowsSystemSound
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFilename = 0x00020000;
    private const uint SndAlias = 0x00010000;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);
    private static readonly object Sync = new();
    private static DateTimeOffset _lastPlayed = DateTimeOffset.MinValue;

    public static void PlayNotification()
    {
        lock (Sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPlayed < Cooldown)
                return;
            _lastPlayed = now;
        }

        var configuredSound = GetConfiguredNotificationSound();
        var played = configuredSound is not null
            ? PlaySound(configuredSound, IntPtr.Zero, SndAsync | SndNoDefault | SndFilename)
            : PlaySound("Notification.Default", IntPtr.Zero, SndAsync | SndNoDefault | SndAlias);
        Log.Info(played
            ? "Windows Notification.Default sound started."
            : "Windows did not provide a playable Notification.Default sound.");
    }

    internal static string? GetConfiguredNotificationSound()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"AppEvents\Schemes\Apps\.Default\Notification.Default\.Current");
            var path = Environment.ExpandEnvironmentVariables(key?.GetValue(string.Empty) as string ?? string.Empty);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
