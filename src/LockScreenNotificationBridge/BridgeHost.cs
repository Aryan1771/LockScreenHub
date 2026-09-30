using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LockScreenNotificationBridge;

internal sealed class BridgeHost : IAsyncDisposable
{
    private readonly RotatingLog log;
    private readonly SteamSource steam;
    private readonly BrowserSource browsers;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly Dictionary<string, SteamDownload> lastShown = new();
    private readonly HashSet<string> inactiveShown = new(StringComparer.Ordinal);
    private uint sequence;
    private bool registered;

    public BridgeHost(RotatingLog log)
    {
        this.log = log;
        steam = new SteamSource(log, OnSnapshotAsync);
        browsers = new BrowserSource(log, OnBrowserSnapshotAsync);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            AppNotificationManager.Default.Register();
            registered = true;
            log.Info("Windows app notification manager initialized");
        }
        catch (Exception ex)
        {
            log.Error("Windows notifications unavailable", ex);
        }

        var steamTask = steam.RunAsync(cancellationToken);
        var browserTask = browsers.RunAsync(cancellationToken);
        await Task.WhenAll(steamTask, browserTask);
    }

    private async Task OnSnapshotAsync(IReadOnlyList<SteamDownload> downloads)
    {
        if (!registered) return;
        if (downloads.Count == 0)
        {
            foreach (var item in lastShown.Values.Where(x => !x.IsComplete && inactiveShown.Add(x.Id)))
            {
                try
                {
                    var ended = new AppNotificationBuilder()
                        .AddText("Steam activity no longer active")
                        .AddText(item.Name)
                        .BuildNotification();
                    ended.Tag = Tag(item.Id);
                    ended.Group = "steam-downloads";
                    AppNotificationManager.Default.Show(ended);
                    log.Info("Steam download activity ended or paused");
                }
                catch (Exception ex) { log.Error("Steam inactive status notification failed", ex); }
            }
            return;
        }
        var active = downloads.OrderByDescending(x => x.BytesToDownload > 0 ? (double)x.BytesDownloaded / x.BytesToDownload : -1).First();
        var resumed = inactiveShown.Remove(active.Id);
        await updateGate.WaitAsync();
        try
        {
            if (lastShown.TryGetValue(active.Id, out var prior) &&
                active.BytesToDownload == prior.BytesToDownload &&
                Percentage(active) == Percentage(prior) &&
                active.IsComplete == prior.IsComplete &&
                active.IsDownloading == prior.IsDownloading &&
                RateBucket(active) == RateBucket(prior))
                return;

            if (active.IsComplete)
            {
                var complete = new AppNotificationBuilder()
                    .AddText("Steam activity complete")
                    .AddText(active.Name)
                    .BuildNotification();
                complete.Tag = Tag(active.Id);
                complete.Group = "steam-downloads";
                AppNotificationManager.Default.Show(complete);
                log.Info("Steam activity completed");
            }
            else
            {
                var percent = active.BytesToDownload > 0
                    ? Math.Clamp((double)active.BytesDownloaded / active.BytesToDownload, 0, 1)
                    : 0;
                var data = new AppNotificationProgressData(++sequence)
                {
                    Title = "Steam activity",
                    Status = SteamStatus(active),
                    Value = percent,
                    ValueStringOverride = active.BytesToDownload > 0 ? $"{percent:P0} · {FormatBytes(active.BytesDownloaded)} / {FormatBytes(active.BytesToDownload)}" : "Progress unavailable"
                };

                if (!lastShown.ContainsKey(active.Id) || resumed)
                {
                    var notification = new AppNotificationBuilder()
                        .AddText("Steam activity")
                        .AddText(active.Name)
                        .AddProgressBar(new AppNotificationProgressBar().BindTitle().BindStatus().BindValue().BindValueStringOverride())
                        .BuildNotification();
                    notification.Tag = Tag(active.Id);
                    notification.Group = "steam-downloads";
                    notification.Progress = data;
                    AppNotificationManager.Default.Show(notification);
                }
                else
                {
                    var result = await AppNotificationManager.Default.UpdateAsync(data, Tag(active.Id), "steam-downloads");
                    log.Info($"Steam progress update result: {result}; reported={Percentage(active)}%");
                    if (result != AppNotificationProgressResult.Succeeded)
                    {
                        AppNotificationManager.Default.Show(BuildSteamProgressNotification(active, data));
                        log.Info("Steam progress card recreated after Windows update was unavailable");
                    }
                }
                log.Info("Steam activity notification updated");
            }
            lastShown[active.Id] = active;
        }
        catch (Exception ex) { log.Error("Steam notification update failed", ex); }
        finally { updateGate.Release(); }
    }

    private AppNotification BuildSteamProgressNotification(SteamDownload active, AppNotificationProgressData data)
    {
        var notification = new AppNotificationBuilder()
            .AddText("Steam activity")
            .AddText(active.Name)
            .AddProgressBar(new AppNotificationProgressBar().BindTitle().BindStatus().BindValue().BindValueStringOverride())
            .BuildNotification();
        notification.Tag = Tag(active.Id);
        notification.Group = "steam-downloads";
        notification.Progress = data;
        return notification;
    }

    private async Task OnBrowserSnapshotAsync(IReadOnlyList<BrowserDownloadActivity> activities)
    {
        if (!registered || activities.Count == 0) return;
        try
        {
            var files = activities.Sum(x => x.FileCount);
            var bytes = activities.Aggregate(0UL, (sum, item) => sum + item.BytesReceived);
            var browsers = string.Join(", ", activities.Select(x => x.Browser).Distinct(StringComparer.OrdinalIgnoreCase));
            var notification = new AppNotificationBuilder()
                .AddText(browsers)
                .AddText($"{files} active file{(files == 1 ? "" : "s")} · {FormatBytes(bytes)} received")
                .BuildNotification();
            notification.Tag = "browser-downloads";
            notification.Group = "file-activity";
            AppNotificationManager.Default.Show(notification);
            log.Info($"Browser download card refreshed: {files} file(s), {FormatBytes(bytes)} observed");
        }
        catch (Exception ex) { log.Error("Browser download notification failed", ex); }
        await Task.CompletedTask;
    }

    private static string Tag(string id) => "steam-" + id;
    private static int Percentage(SteamDownload item) => item.BytesToDownload == 0 ? 0 : (int)Math.Clamp((double)item.BytesDownloaded / item.BytesToDownload * 100, 0, 100);
    private static int RateBucket(SteamDownload item) => item.SteamTotalRateMbps is { } rate ? (int)Math.Round(rate * 10) : -1;
    private static string SteamStatus(SteamDownload item) => !item.IsDownloading
        ? "Paused or waiting"
        : item.SteamTotalRateMbps is > 0 ? $"Downloading · Steam total {item.SteamTotalRateMbps:0.0} Mbps" : "Downloading";
    private static string FormatBytes(ulong bytes) => $"{bytes / (1024d * 1024 * 1024):0.0} GB";

    public ValueTask DisposeAsync()
    {
        steam.Dispose();
        browsers.Dispose();
        if (registered)
        {
            try { AppNotificationManager.Default.Unregister(); } catch { }
        }
        updateGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
