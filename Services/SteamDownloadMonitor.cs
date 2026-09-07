using LockScreenHub.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LockScreenHub.Services;

/// <summary>
/// Initial Steam provider. It intentionally does not modify Steam files or inject into Steam.
/// The provider watches configurable Steam library locations and can be extended with
/// a Steam-specific integration once the download metadata source is selected.
/// </summary>
public sealed class SteamDownloadMonitor
{
    public event EventHandler<DownloadProgress>? ProgressChanged;

    private CancellationTokenSource? _cts;

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _ = MonitorAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        // Phase 1 deliberately leaves Steam discovery passive.
        // We will replace this with a real Steam provider in Phase 3.
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
}
