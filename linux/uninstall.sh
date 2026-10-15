#!/usr/bin/env bash
set -euo pipefail

systemctl --user disable --now lockscreen-notification-bridge.service 2>/dev/null || true
rm -f "$HOME/.config/systemd/user/lockscreen-notification-bridge.service"
rm -rf "$HOME/.local/lib/lockscreen-notification-bridge"
systemctl --user daemon-reload
echo "The Linux bridge service and program were removed. Diagnostic logs were kept in ~/.local/state/lockscreen-notification-bridge."
