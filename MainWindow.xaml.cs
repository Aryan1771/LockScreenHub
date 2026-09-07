using Microsoft.UI.Xaml;
using System;
using Windows.System;
using Windows.UI.Notifications.Management;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LockScreenHub;

public sealed partial class MainWindow : Window
{
    private readonly UserNotificationListener _listener;

    public MainWindow()
    {
        InitializeComponent();
        _listener = UserNotificationListener.Current;
        RefreshAccessStatus();
    }

    private void RefreshAccessStatus()
    {
        try
        {
            var status = _listener.GetAccessStatus();
            AccessStatusText.Text = status switch
            {
                UserNotificationListenerAccessStatus.Allowed =>
                    "Allowed — LockScreen Hub can read Windows notifications.",
                UserNotificationListenerAccessStatus.Denied =>
                    "Denied — enable notification access in Windows Settings.",
                _ =>
                    $"Status: {status}"
            };
        }
        catch (Exception ex)
        {
            AccessStatusText.Text = $"Notification listener unavailable: {ex.Message}";
        }
    }

    private async void GrantNotificationAccess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await _listener.RequestAccessAsync();
            AccessStatusText.Text = status switch
            {
                UserNotificationListenerAccessStatus.Allowed =>
                    "Allowed — notification access is enabled.",
                UserNotificationListenerAccessStatus.Denied =>
                    "Denied — Windows did not grant access. You can enable it manually in Settings.",
                _ =>
                    $"Status: {status}"
            };
        }
        catch (Exception ex)
        {
            AccessStatusText.Text = $"Could not request access: {ex.Message}";
        }
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        RefreshAccessStatus();
    }

    private void SendTestNotification_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("LockScreen Hub")
                .AddText("This is a test notification.")
                .AddText("If Windows allows lock-screen notifications, this can be shown while your PC is locked.");

            AppNotificationManager.Default.Show(builder.BuildNotification());
            ResultText.Text = "Test notification sent.";
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Notification failed: {ex.Message}";
        }
    }

    private async void OpenLockScreenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(
                new Uri("ms-settings:personalization-lockscreen"));
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Could not open Settings: {ex.Message}";
        }
    }
}
