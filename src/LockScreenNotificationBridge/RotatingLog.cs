namespace LockScreenNotificationBridge;

internal sealed class RotatingLog : IDisposable
{
    private const long MaxBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockScreenNotificationBridge", "logs", "bridge.log");
    private StreamWriter? writer;

    public RotatingLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Open();
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception ex) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        lock (gate)
        {
            try
            {
                if (new FileInfo(path).Length >= MaxBytes) Rotate();
                writer!.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}{(ex is null ? "" : $" ({ex.GetType().Name}: {ex.Message})")}");
                writer.Flush();
            }
            catch { /* Diagnostics must never stop the bridge. */ }
        }
    }

    private void Open() => writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    private void Rotate()
    {
        writer?.Dispose();
        var backup = path + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        if (File.Exists(path)) File.Move(path, backup);
        Open();
    }
    public void Dispose() { lock (gate) writer?.Dispose(); }
}
