using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace LockScreenHub.Services;

public static class WindowsPolicyService
{
    private const string PersonalizationKey =
        @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    /// <summary>
    /// Best-effort helper for Windows content suggestions. It does not disable Weather.
    /// Registry policies vary by Windows build, so the UI always remains the authoritative
    /// place for lock-screen widget configuration.
    /// </summary>
    public static bool TryDisableConsumerSuggestions()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizationKey);
            if (key is null)
                return false;

            key.SetValue("SubscribedContent-338388Enabled", 0, RegistryValueKind.DWord);
            key.SetValue("SubscribedContent-353694Enabled", 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            // CWE-778: Log registry access errors for security auditing
            Debug.WriteLine($"WindowsPolicyService registry access failed: {ex.GetType().Name} - {ex.Message}");
            return false;
        }
    }
}
