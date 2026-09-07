namespace LockScreenHub.Models;

public sealed record DownloadProgress(
    string Provider,
    string ItemName,
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta)
{
    public double Fraction =>
        TotalBytes <= 0
            ? 0
            : (double)DownloadedBytes / TotalBytes;
}
