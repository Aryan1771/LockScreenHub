using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using LockScreenHub.Models;
using System.Diagnostics;

namespace LockScreenHub.Services;

public sealed class NotificationListenerService : IAsyncDisposable
{
    private readonly UserNotificationListener _listener;
    private volatile bool _started; // Thread-safe field access for CWE-820
    private readonly object _lockObject = new();

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

        lock (_lockObject) // CWE-567, CWE-662: Synchronized access
        {
            _listener.NotificationChanged += OnNotificationChanged;
            _started = true;
        }

        await Task.CompletedTask;
        return true;
    }

    private async void OnNotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args) => await HandleNotificationChangeAsync(sender, args);

    private async Task HandleNotificationChangeAsync(
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

                // CWE-571: Removed redundant condition; Count is guaranteed > 0 here
                var title = texts[0].Text;
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
        catch (Exception ex)
        {
            // CWE-778: Log notification access errors for security auditing
            Debug.WriteLine($"NotificationListenerService error: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lockObject) // CWE-567, CWE-662: Synchronized access
        {
            if (_started)
                _listener.NotificationChanged -= OnNotificationChanged;
        }

        return ValueTask.CompletedTask;
    }
}
