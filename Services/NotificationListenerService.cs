using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications.Management;
using LockScreenHub.Models;

namespace LockScreenHub.Services;

public sealed class NotificationListenerService : IAsyncDisposable
{
    private readonly UserNotificationListener _listener;
    private bool _started;

    public event EventHandler<UnifiedNotification>? NotificationReceived;

    public NotificationListenerService()
    {
        _listener = UserNotificationListener.Current;
    }

    public async Task<bool> StartAsync()
    {
        var status = _listener.GetAccessStatus();

        if (status != UserNotificationListenerAccessStatus.Allowed)
            return false;

        _listener.NotificationChanged += OnNotificationChanged;
        _started = true;

        await Task.CompletedTask;
        return true;
    }

    private async void OnNotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        try
        {
            var notifications =
                await sender.GetNotificationsAsync(NotificationKinds.Toast);

            foreach (var notification in notifications)
            {
                var binding = notification.Notification.Visual.GetBinding(
                    Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);

                if (binding is null)
                    continue;

                var texts = binding.GetTextElements();
                if (texts.Count == 0)
                    continue;

                var title = texts.Count > 0 ? texts[0].Text : "";
                var body = texts.Count > 1 ? texts[1].Text : "";

                var appName = notification.AppInfo.DisplayInfo.DisplayName;

                NotificationReceived?.Invoke(
                    this,
                    new UnifiedNotification(
                        appName,
                        appName,
                        title,
                        body));
            }
        }
        catch
        {
            // Notification access can be revoked at any time.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_started)
            _listener.NotificationChanged -= OnNotificationChanged;

        return ValueTask.CompletedTask;
    }
}
