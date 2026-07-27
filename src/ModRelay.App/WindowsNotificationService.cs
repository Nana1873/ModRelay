using ModRelay.Core;

namespace ModRelay.App;

internal sealed class WindowsNotificationService
{
    private readonly NotifyIcon _fallbackIcon;
    private readonly DesktopNotificationManager _desktopNotifications = new();

    public WindowsNotificationService(NotifyIcon fallbackIcon)
    {
        _fallbackIcon = fallbackIcon;
    }

    public void Show(string title, string message, bool isError = false, bool playSound = true)
    {
        try
        {
            _desktopNotifications.Show(title, message, isError);
            Log.Info($"Desktop notification displayed: {title}");
        }
        catch (Exception ex)
        {
            Log.Warn("The desktop notification could not be shown; using a tray balloon instead.", ex);
            ShowFallback(title, message, isError);
        }

        if (playSound)
            WindowsSystemSound.PlayNotification();
    }

    private void ShowFallback(string title, string message, bool isError)
    {
        if (!_fallbackIcon.Visible)
            return;

        _fallbackIcon.BalloonTipTitle = title;
        _fallbackIcon.BalloonTipText = message;
        _fallbackIcon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
        _fallbackIcon.ShowBalloonTip(5000);
    }
}
