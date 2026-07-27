using ModRelay.App;

namespace ModRelay.Tests;

public sealed class DesktopNotificationTests
{
    [Fact]
    public void NotificationWindow_IsVisibleAboveAppsWithoutTakingFocus()
    {
        using var notification = new DesktopNotificationWindow(
            "Imported", "Example mod", isError: false, Screen.PrimaryScreen!);

        Assert.True(notification.TopMost);
        Assert.False(notification.ShowInTaskbar);
        Assert.True(notification.DoesNotActivate);
        Assert.Equal(FormBorderStyle.None, notification.FormBorderStyle);
    }

    [Fact]
    public void ConfiguredWindowsNotificationSoundExistsWhenConfigured()
    {
        var sound = WindowsSystemSound.GetConfiguredNotificationSound();

        Assert.True(sound is null || File.Exists(sound));
    }
}
