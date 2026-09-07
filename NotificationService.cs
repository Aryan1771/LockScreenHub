using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Threading.Tasks;

namespace LockScreenHub;

public static class NotificationService
{
    public static void ShowMessage(
        string appName,
        string title,
        string body)
    {
        var notification = new AppNotificationBuilder()
            .AddText(appName)
            .AddText(title)
            .AddText(body)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }

    public static void ShowProgress(
        string appName,
        string title,
        double progress,
        string status,
        string valueText)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);

        var bar = new AppNotificationProgressBar()
            .SetTitle(title)
            .SetValue(progress)
            .SetStatus(status)
            .SetValueStringOverride(valueText);

        var notification = new AppNotificationBuilder()
            .AddText(appName)
            .AddProgressBar(bar)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }
}
