using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using System;

namespace LockScreenHub;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
        }
        catch
        {
            // Notification registration is optional for the UI to start.
        }

        _window = new MainWindow();
        _window.Activate();
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        // Future: route notification button actions here.
    }
}
