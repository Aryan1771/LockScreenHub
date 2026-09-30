using System.Text.Json;
using System.Text.RegularExpressions;

namespace LockScreenNotificationBridge;

internal sealed record BrowserDownloadActivity(string Browser, int FileCount, ulong BytesReceived);

/// <summary>
/// Best-effort local download observation. This watches browser partial files only;
/// it never opens downloaded content, history databases, messages, or cookies.
/// </summary>
internal sealed class BrowserSource : IDisposable
{
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".opdownload", ".download"];
    private readonly RotatingLog log;
    private readonly Func<IReadOnlyList<BrowserDownloadActivity>, Task> onSnapshot;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly Dictionary<string, (int Count, ulong Bytes)> previous = new(StringComparer.OrdinalIgnoreCase);
    private Timer? timer;
    private CancellationToken token;
    private DateTimeOffset lastNotification = DateTimeOffset.MinValue;

    public BrowserSource(RotatingLog log, Func<IReadOnlyList<BrowserDownloadActivity>, Task> onSnapshot)
    {
        this.log = log;
        this.onSnapshot = onSnapshot;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        token = cancellationToken;
        foreach (var directory in DiscoverDirectories())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watchers.Add(watcher);
            }
            catch (Exception ex) { log.Error("Browser download folder watcher unavailable", ex); }
        }

        log.Info($"Browser download monitoring initialized for {watchers.Count} folders");
        timer = new Timer(async _ => await SafeScanAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => RequestScan();
    private void OnFileRenamed(object sender, RenamedEventArgs e) => RequestScan();
    private void RequestScan() => timer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    private async Task SafeScanAsync()
    {
        if (token.IsCancellationRequested || !await scanGate.WaitAsync(0)) return;
        try
        {
            // Refresh discovery periodically so new profiles or configured target folders are found.
            var roots = DiscoverDirectories();
            var byBrowser = new Dictionary<string, (int Count, ulong Bytes)>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string label = BrowserLabel(root);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root))
                    {
                        if (!PartialExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var info = new FileInfo(file);
                            var current = byBrowser.GetValueOrDefault(label);
                            byBrowser[label] = (current.Count + 1, current.Bytes + (ulong)Math.Max(0, info.Length));
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            foreach (var item in byBrowser)
            {
                var old = previous.GetValueOrDefault(item.Key);
                var current = item.Value;
                if (current == old) continue;

                // One card is replaced by its stable tag. Suppress tiny/high-frequency file churn.
                var materiallyChanged = old.Count == 0 || current.Count != old.Count ||
                    (current.Bytes > old.Bytes && current.Bytes - old.Bytes >= 5UL * 1024 * 1024);
                if (!materiallyChanged) { previous[item.Key] = current; continue; }
                if (DateTimeOffset.UtcNow - lastNotification < TimeSpan.FromSeconds(15)) continue;
                previous[item.Key] = current;
                lastNotification = DateTimeOffset.UtcNow;
                await onSnapshot([new BrowserDownloadActivity(item.Key, current.Count, current.Bytes)]);
            }

            foreach (var oldLabel in previous.Keys.Except(byBrowser.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
                previous[oldLabel] = (0, 0);
        }
        catch (Exception ex) { log.Error("Browser download scan failed", ex); }
        finally { scanGate.Release(); }
    }

    private IEnumerable<string> DiscoverDirectories()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) folders.Add(downloads);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddChromium(local, @"Google\Chrome\User Data");
        AddChromium(local, @"Microsoft\Edge\User Data");
        AddChromium(local, @"BraveSoftware\Brave-Browser\User Data");
        AddChromium(local, @"Vivaldi\User Data");
        AddChromium(local, @"Yandex\YandexBrowser\User Data");
        AddChromium(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera Stable");
        AddChromium(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera GX Stable");

        var firefoxRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
        var profilesIni = Path.Combine(firefoxRoot, "profiles.ini");
        if (File.Exists(profilesIni))
        {
            try
            {
                string? profilePath = null;
                bool relative = true;
                foreach (var line in File.ReadLines(profilesIni).Append(string.Empty))
                {
                    if (line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase) || line.Length == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(profilePath))
                        {
                            var profile = relative ? Path.Combine(firefoxRoot, profilePath) : profilePath;
                            AddFirefoxDownload(profile, folders);
                        }
                        profilePath = null;
                        relative = true;
                    }
                    else if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) profilePath = line[5..].Trim();
                    else if (line.Equals("IsRelative=0", StringComparison.OrdinalIgnoreCase)) relative = false;
                }
            }
            catch (IOException) { }
        }

        return folders;

        void AddChromium(string baseRoot, string relativeRoot)
        {
            var userData = Path.Combine(baseRoot, relativeRoot);
            if (!Directory.Exists(userData)) return;
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(userData))
                {
                    var preferences = Path.Combine(profile, "Preferences");
                    if (!File.Exists(preferences)) continue;
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(preferences));
                        if (json.RootElement.TryGetProperty("download", out var download) &&
                            download.TryGetProperty("default_directory", out var target) &&
                            target.ValueKind == JsonValueKind.String && target.GetString() is { Length: > 0 } value && Directory.Exists(value))
                            folders.Add(value);
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AddFirefoxDownload(string profile, HashSet<string> folders)
    {
        var prefs = Path.Combine(profile, "prefs.js");
        if (!File.Exists(prefs)) return;
        try
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(prefs), "user_pref\\(\\\"browser\\.download\\.dir\\\",\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"\\)", RegexOptions.IgnoreCase))
            {
                var value = Regex.Unescape(match.Groups[1].Value);
                if (Directory.Exists(value)) folders.Add(value);
            }
        }
        catch (IOException) { }
    }

    private static string BrowserLabel(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome download";
        if (p.Contains("Microsoft\\Edge", StringComparison.OrdinalIgnoreCase)) return "Edge download";
        if (p.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "Brave download";
        if (p.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "Vivaldi download";
        if (p.Contains("Yandex", StringComparison.OrdinalIgnoreCase)) return "Yandex download";
        if (p.Contains("Firefox", StringComparison.OrdinalIgnoreCase) || p.Contains("Mozilla", StringComparison.OrdinalIgnoreCase)) return "Firefox download";
        if (p.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "Opera download";
        return "Browser download";
    }

    public void Dispose()
    {
        timer?.Dispose();
        foreach (var watcher in watchers) watcher.Dispose();
        scanGate.Dispose();
    }
}
