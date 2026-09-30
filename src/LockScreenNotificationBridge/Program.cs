using LockScreenNotificationBridge;
using System.Security.Principal;

var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
using var instanceMutex = new Mutex(initiallyOwned: true, $@"Local\LockScreenNotificationBridge-{sid}", out var isFirstInstance);
if (!isFirstInstance) return;
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => lifetime.Cancel();

try
{
    using var log = new RotatingLog();
    log.Info("Process started");
    await using var bridge = new BridgeHost(log);
    await bridge.RunAsync(lifetime.Token);
    log.Info("Process shutting down");
}
catch (Exception ex)
{
    // Never record notification contents. Exception text is limited to diagnostic details.
    try { new RotatingLog().Error("Fatal process error", ex); } catch { }
}
