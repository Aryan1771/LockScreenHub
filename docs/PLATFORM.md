# Windows capability notes

Initial development environment: Windows 11 Home Single Language, OS build 26200, x64. Later observations are recorded in the [root README](../README.md); these are historical environment records, not current-host guarantees.

## Supported behavior used

- Windows App SDK local app notifications are supported for unpackaged desktop apps. The project uses `AppNotificationManager` and its progress data update API. Microsoft documents this API for unpackaged Win32/.NET applications and explicitly supports updating a progress notification in place.
- Notification Center delivery is the system notification path. Toast visibility, lock-screen exposure, and privacy are governed by per-app and Windows user settings.
- Startup is a per-user Task Scheduler logon task, limited to the current account. No service or elevation is needed.

## Deliberately unsupported/omitted

- Secure lock-screen replacement or overlay: prohibited and not implemented.
- Guaranteed lock-screen toast visibility: cannot be controlled by this process; user/system settings decide.
- Reading other apps' notification content: Windows' listener requires a manifest capability and explicit access request. Omitted; the background process must not read WhatsApp messages or other private content.
- WhatsApp forwarding: not needed when WhatsApp already emits its own Windows notification. Native notification handling remains untouched.
- Microsoft Store progress: no reliable general-purpose local progress source is implemented. No Store values are fabricated.
- Steam phase accuracy: local ACF manifest byte fields are best-effort implementation details, not a stable public API. They do not fully identify queue state, paused/resumed state, verification, patching, or completion. The source also reads `content_log.txt` for state transitions, byte snapshots, and recent Steam-wide rate samples. These sources remain best-effort observations, not a stable per-game telemetry API.
- Download completion: not surfaced yet. A manifest disappearing or byte counters resetting cannot safely distinguish completion from pause/removal/Steam maintenance.

## References

- [Windows notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/)
- [Windows App SDK app notifications for .NET](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
- [Progress bar data binding and updates](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-progress-bar)
- [Notification listener capability and access request](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)
- [Windows app packaging and package identity](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/)
