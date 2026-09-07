# LockScreen Hub

A Windows 11 WinUI 3 application intended to:

- Keep the native Windows Weather lock-screen information.
- Read Windows notifications after explicit user consent.
- Re-publish selected useful notifications through Windows App Notifications.
- Provide a foundation for Steam download progress and provider-specific filtering.
- Avoid replacing or bypassing the Windows secure lock screen.

## Current status

Phase 1/2 prototype:

1. WinUI 3 settings UI.
2. User Notification Listener capability.
3. User consent flow.
4. Notification listener service.
5. Test notification.
6. Progress notification helper.
7. Windows lock-screen Settings shortcut.

Steam, WhatsApp-specific filtering, and granular removal of Microsoft's non-weather lock-screen cards are deliberately not claimed as complete yet. Those pieces require build-specific Windows integration and provider testing.

## Build

Recommended environment:

- Windows 11 24H2/25H2/26H1-era SDK
- Visual Studio 2026
- .NET 10 SDK
- Windows App SDK 2.1
- Windows SDK 10.0.26100+

Open `LockScreenHub.csproj` in Visual Studio, restore NuGet packages, select x64, then Build and Run.

Because `userNotificationListener` is a privacy-sensitive capability, Windows requires user consent before notifications can be read.

## Important

This project does not create a fake lock screen, hook Winlogon, or draw an overlay over the secure desktop.
