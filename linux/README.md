# Linux port

This is a separate Python 3 implementation for Arch Linux and systemd based Arch derivatives such as Manjaro, EndeavourOS, and Garuda. It runs as a user service inside the graphical session and sends notifications through `org.freedesktop.Notifications`. It does not need root privileges or a custom lock screen.

## Install

Install Python and GLib's `gdbus` client if needed:

```sh
sudo pacman -S python glib2
```

From this directory, run:

```sh
chmod +x install.sh uninstall.sh bridge.py
./install.sh
```

The service starts at user login. Logs are kept under `~/.local/state/lockscreen-notification-bridge/`. Notification replacement IDs stay in memory for the current desktop notification service session.

## Sources and behavior

- Steam: reads local app manifests and `content_log.txt`. A card is emitted only when an active download state is recorded after the current Steam process started. Manifests alone never create an active card. Steam's local files are an undocumented, best-effort source.
- Browsers: watches `.crdownload`, `.part`, `.download`, and `.opdownload` files in common and discoverable Chrome, Chromium, Edge, Brave, Vivaldi, Opera, and Firefox download folders. It reports observed bytes without inventing a total or percentage. A browser process must be running to avoid stale partial files causing a startup card.
- Uploads: the notification renderer supports upload progress and is exercised by `--self-test-notifications`. Browsers do not expose a general OS level upload-progress feed to this service, so real uploads from arbitrary websites are not currently discoverable. No page contents, credentials, or browser history are read.

## Notification and lock screen checks

Run this from a logged-in graphical terminal to exercise notification creation, in-place replacement, progress changes, and completion for simulated download and upload events:

```sh
~/.local/lib/lockscreen-notification-bridge/bridge.py --self-test-notifications
```

Desktop notification delivery uses the standard Freedesktop D-Bus notification API. The server returns a notification ID; later progress events pass that ID as `replaces_id`, so the same card is updated where the desktop notification server honors replacement. Progress is included in the text and an optional `value` hint; visual progress bars are desktop specific.

The bridge cannot force notifications onto a lock screen. GNOME and KDE each have notification privacy/settings controls, and some sessions suppress notifications while locked. Enable lock-screen notification content in the desktop's notification settings, then lock the machine and run a real transfer to verify behavior. Do not test by locking the machine before saving work.

## Tests

The standard library test suite does not send real desktop notifications; it checks D-Bus creation/replacement arguments, simulated download and upload progression, browser partial-file scanning, Steam current-session freshness, and stale-manifest suppression:

```sh
python3 -m unittest discover -s tests -v
```

Uninstall the program and service with `./uninstall.sh`. Diagnostic logs are retained.
