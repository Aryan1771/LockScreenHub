namespace LockScreenHub.Models;

public sealed record UnifiedNotification(
    string Provider,
    string AppName,
    string Title,
    string Body,
    bool Sensitive = false);
