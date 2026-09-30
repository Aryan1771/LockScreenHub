using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LockScreenNotificationBridge;

internal sealed record SteamDownload(string Id, string Name, ulong BytesDownloaded, ulong BytesToDownload, bool IsComplete, bool IsDownloading = false, double? SteamTotalRateMbps = null);

internal sealed class SteamSource : IDisposable
{
    private readonly RotatingLog log;
    private readonly Func<IReadOnlyList<SteamDownload>, Task> onSnapshot;
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private Timer? timer;
    private CancellationToken cancellationToken;
    private bool steamDetected;
    private DateTime contentLogWriteUtc = DateTime.MinValue;
    private Dictionary<string, (ulong Downloaded, ulong Total, bool IsDownloading)> cachedContentLog = new(StringComparer.Ordinal);
    private double? cachedDownloadRateMbps;
    private DateTime? cachedDownloadRateTime;

    public SteamSource(RotatingLog log, Func<IReadOnlyList<SteamDownload>, Task> onSnapshot)
    {
        this.log = log;
        this.onSnapshot = onSnapshot;
    }

    public async Task RunAsync(CancellationToken token)
    {
        cancellationToken = token;
        ConfigureWatchers();
        timer = new Timer(async _ => await SafeScanAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ConfigureWatchers()
    {
        foreach (var root in FindLibraryRoots())
        {
            var manifests = Path.Combine(root, "steamapps");
            try
            {
                if (!Directory.Exists(manifests)) continue;
                var watcher = new FileSystemWatcher(manifests, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Renamed += OnRenamed;
                watchers.Add(watcher);
            }
            catch (Exception ex) { log.Error("Steam library watcher unavailable", ex); }

            var logs = Path.Combine(root, "logs");
            try
            {
                if (Directory.Exists(logs) && File.Exists(Path.Combine(logs, "content_log.txt")))
                {
                    var watcher = new FileSystemWatcher(logs, "content_log.txt")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += OnChanged;
                    watchers.Add(watcher);
                }
            }
            catch (Exception ex) { log.Error("Steam content-log watcher unavailable", ex); }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => DebounceScan();
    private void OnRenamed(object sender, RenamedEventArgs e) => DebounceScan();
    private void DebounceScan() => timer?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    private async Task SafeScanAsync()
    {
        if (cancellationToken.IsCancellationRequested || !await scanGate.WaitAsync(0)) return;
        try
        {
            var roots = FindLibraryRoots();
            var running = IsSteamRunning();
            if (running && !steamDetected) { steamDetected = true; log.Info("Steam detected"); }
            if (!running && steamDetected) { steamDetected = false; log.Info("Steam is not running"); }

            var logState = ReadContentLog(roots);
            var downloads = roots.SelectMany(ReadLibrary).Select(d =>
            {
                if (!logState.TryGetValue(d.Id, out var state)) return d;
                var downloaded = Math.Max(d.BytesDownloaded, state.Downloaded);
                var total = Math.Max(d.BytesToDownload, state.Total);
                var rateIsFresh = cachedDownloadRateTime is { } rateTime && DateTime.Now - rateTime <= TimeSpan.FromSeconds(90);
                return d with { BytesDownloaded = downloaded, BytesToDownload = total, IsComplete = total > 0 && downloaded >= total, IsDownloading = state.IsDownloading, SteamTotalRateMbps = state.IsDownloading && rateIsFresh ? cachedDownloadRateMbps : null };
            }).ToArray();
            var logActive = downloads.Where(d => d.IsDownloading && !d.IsComplete && d.BytesToDownload > 0).ToArray();
            var active = logActive.Length > 0 ? logActive : downloads.Where(d => !d.IsComplete && d.BytesToDownload > 0 && d.BytesDownloaded > 0).ToArray();
            await onSnapshot(active);
        }
        catch (Exception ex) { log.Error("Steam source scan failed", ex); }
        finally
        {
            scanGate.Release();
            var running = IsSteamRunning();
            timer?.Change(running ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2), running ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2));
        }
    }

    private static bool IsSteamRunning()
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process) running = true;
        }
        return running;
    }

    private static IEnumerable<SteamDownload> ReadLibrary(string library)
    {
        var apps = Path.Combine(library, "steamapps");
        if (!Directory.Exists(apps)) yield break;
        foreach (var path in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
        {
            string text;
            try { text = File.ReadAllText(path); } catch { continue; }
            var id = Field(text, "appid") ?? Path.GetFileNameWithoutExtension(path).Replace("appmanifest_", "");
            var name = Field(text, "name") ?? "Steam download";
            if (!ulong.TryParse(Field(text, "BytesDownloaded"), out var downloaded) ||
                !ulong.TryParse(Field(text, "BytesToDownload"), out var total)) continue;
            var complete = total > 0 && downloaded >= total;
            yield return new SteamDownload(id, name, downloaded, total, complete);
        }
    }

    private Dictionary<string, (ulong Downloaded, ulong Total, bool IsDownloading)> ReadContentLog(IEnumerable<string> roots)
    {
        var paths = roots.Select(root => Path.Combine(root, "logs", "content_log.txt"))
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var newestWrite = paths.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        if (newestWrite <= contentLogWriteUtc) return cachedContentLog;
        var latestState = new Dictionary<string, bool>(StringComparer.Ordinal);
        var latestProgress = new Dictionary<string, (ulong Downloaded, ulong Total)>(StringComparer.Ordinal);
        double? latestRate = null;
        foreach (var path in paths)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var state = Regex.Match(line, @"AppID\s+(\d+)\s+App update changed\s*:\s*(.*)", RegexOptions.IgnoreCase);
                    if (state.Success)
                    {
                        var detail = state.Groups[2].Value;
                        latestState[state.Groups[1].Value] = detail.Contains("Running Update", StringComparison.OrdinalIgnoreCase) &&
                            detail.Contains("Downloading", StringComparison.OrdinalIgnoreCase);
                    }

                    var progress = Regex.Match(line, @"AppID\s+(\d+)\s+update started\s*:\s*download\s+(\d+)/(\d+)", RegexOptions.IgnoreCase);
                    if (progress.Success && ulong.TryParse(progress.Groups[2].Value, out var received) && ulong.TryParse(progress.Groups[3].Value, out var total))
                        latestProgress[progress.Groups[1].Value] = (received, total);

                    var rate = Regex.Match(line, @"Current download rate:\s*([0-9]+(?:\.[0-9]+)?)\s*Mbps", RegexOptions.IgnoreCase);
                    if (rate.Success && double.TryParse(rate.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mbps))
                    {
                        latestRate = mbps;
                        var timestamp = Regex.Match(line, @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]");
                        if (timestamp.Success && DateTime.TryParseExact(timestamp.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var rateTime))
                            cachedDownloadRateTime = rateTime;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        cachedContentLog = latestProgress.ToDictionary(
            x => x.Key,
            x => (x.Value.Downloaded, x.Value.Total, latestState.GetValueOrDefault(x.Key)),
            StringComparer.Ordinal);
        contentLogWriteUtc = newestWrite;
        cachedDownloadRateMbps = latestRate;
        return cachedContentLog;
    }

    private static string? Field(string text, string name)
    {
        var match = Regex.Match(text, $"\\\"{Regex.Escape(name)}\\\"\\s+\\\"([^\\\"]*)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IEnumerable<string> FindLibraryRoots()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { @"HKCU:\Software\Valve\Steam", @"HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", @"HKLM:\SOFTWARE\Valve\Steam" })
        {
            try
            {
                var install = Microsoft.Win32.Registry.GetValue(key.Replace("HKCU:", "HKEY_CURRENT_USER").Replace("HKLM:", "HKEY_LOCAL_MACHINE"), "InstallPath", null) as string;
                if (!string.IsNullOrWhiteSpace(install)) candidates.Add(install);
            }
            catch { }
        }
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultPath)) candidates.Add(defaultPath);

        foreach (var root in candidates.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                    candidates.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
        }
        return candidates.Where(Directory.Exists);
    }

    public void Dispose()
    {
        timer?.Dispose();
        foreach (var watcher in watchers) watcher.Dispose();
        scanGate.Dispose();
    }
}
