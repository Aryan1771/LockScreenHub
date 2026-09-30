# LockScreen Notification Bridge

A session-scoped, windowless Windows background process. Steam download status and observable browser partial downloads are surfaced as Windows app notifications. No fake lock screen, system-wide service, or cloud component is included.

## Important platform limits

Windows owns lock-screen presentation. A local app notification can be delivered to Windows Notification Center; whether it appears while locked depends on the user's Windows notification, lock-screen privacy, and app settings. This project cannot guarantee or force lock-screen display. Set **Settings → System → Notifications → Lock screen** as desired and allow notification content on the lock screen. Verify the result on this specific PC.

Windows' `UserNotificationListener` can read notifications from other apps only with the declared capability and user consent. It is intentionally not used: silently reading WhatsApp or other notification content would violate the privacy/authorization requirements, and a no-UI background process has no suitable consent flow. Existing WhatsApp notifications therefore pass through natively without bridge duplication. WhatsApp file downloads written into shared folders cannot be reliably distinguished from files saved by browsers, and WhatsApp's private app storage is not inspected; the bridge does not falsely label these as WhatsApp downloads.

Steam's `appmanifest_*.acf` files expose app names and byte counters, but they are implementation details rather than a supported public Steam progress API. Values can lag or be absent, and they don't reliably distinguish downloading from patching/verification. The bridge also watches Steam's local `content_log.txt` for download-state transitions, byte snapshots, and recent Steam-wide download-rate samples. It updates the card on pause/resume and rate changes, labeling the rate as Steam-wide rather than attributing it to a specific game. Exact percentage updates remain limited by the cadence of Steam's own byte snapshots; no percentage is estimated. Windows' progress-update result is checked; if the card is gone or the update is unsupported, the bridge resends a notification with the same tag/group so Notification Center replaces the prior card. Store progress is not implemented because there is no generally available local API that this app can use to read it reliably without elevated or unsupported techniques.

Browser monitoring watches temporary partial files in the common Downloads folder and configured default download folders for Chrome, Edge, Firefox, Brave, Vivaldi, Opera, and Yandex profiles when those folders are discoverable. It reports active file count and bytes observed with no percentage because browser temp files generally don't expose a reliable total. Custom browser destinations that change after startup may not be discovered until restart. Downloads that do not use recognizable partial-file extensions, including some extension-managed and in-app downloads, cannot be guaranteed.

## Build

Requirements: Windows 10 19041 or later, .NET 8 SDK or later, network access on first build for NuGet restore. The published folder bundles the .NET and Windows App SDK runtimes.

```powershell
dotnet publish .\src\LockScreenNotificationBridge\LockScreenNotificationBridge.csproj -c Release -r win-x64 -o .\publish
```

The output is a self-contained `WinExe` (no console window); the publish folder includes the required runtimes.

## Install / uninstall

Run the scripts from an elevated or ordinary PowerShell session; administrator rights are not required. Installation copies the published files to `%LOCALAPPDATA%\Programs\LockScreenNotificationBridge` and registers a per-user logon scheduled task. It does not run the bridge or request notification-listener access. Sign out/in, or run the installed executable once to start it now. Windows may show or suppress the first notification based on notification settings.

```powershell
.\scripts\Install.ps1 -PublishDirectory .\publish
```

Uninstall removes the task, stops this user's process, and deletes only the bridge's own install and log directories:

```powershell
.\scripts\Uninstall.ps1
```

## Diagnostics

Logs are written under `%LOCALAPPDATA%\LockScreenNotificationBridge\logs\bridge.log`, rotated at 1 MiB with one backup. Log entries contain lifecycle/source errors and progress percentages/byte totals only; no message, downloaded file contents, or notification body is collected. Steam uses a file watcher plus 30-second reconciliation timer, slowing to a 2-minute check if Steam isn't running. Browser source folders use file watchers plus a 10-second reconciliation timer, with notifications rate-limited to avoid churn.

## Current status and verification

The source project builds and publishes successfully. It is installed for the current Windows user as a Task Scheduler logon task. After the user's Windows 26H2 setup/reboot, Windows reported build 26300. The current installed process initializes Windows app notifications, logs one browser watch folder, and runs without a window. Steam's local content log is used to improve on potentially stale manifest counters; a fresh Steam download and Notification Center progress update have not been observed end-to-end after this change. Browser notifications have not been observed with a real partial file. Lock-screen presentation and WhatsApp/Store progress remain **unverified/not implemented** (WhatsApp media cannot be safely attributed without an app-supported event or consented notification listener; Store progress has no reliable general local source). Prior idle measurement before browser monitoring was approximately 50 MiB working set and 0.14 CPU seconds over 15 seconds. The project does not provide a signed MSIX installer; the install script deploys the published folder.

The current machine reports Windows 11 Home Single Language, version 10.0.26300, 64-bit. The bridge does not modify weather, lock-screen content, security settings, or other users' profiles.
