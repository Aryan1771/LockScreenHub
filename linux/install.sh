#!/usr/bin/env bash
set -euo pipefail

if [[ ! -x "$(command -v python3 || true)" ]]; then
  echo "python3 is required (Arch: sudo pacman -S python)." >&2
  exit 1
fi
if [[ ! -x "$(command -v gdbus || true)" ]]; then
  echo "gdbus is required (Arch: sudo pacman -S glib2)." >&2
  exit 1
fi
if [[ -z "${DBUS_SESSION_BUS_ADDRESS:-}" && -z "${XDG_RUNTIME_DIR:-}" ]]; then
  echo "Run this from your logged-in graphical desktop session." >&2
  exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
install_dir="$HOME/.local/lib/lockscreen-notification-bridge"
service_dir="$HOME/.config/systemd/user"
state_dir="${XDG_STATE_HOME:-$HOME/.local/state}/lockscreen-notification-bridge"
mkdir -p "$install_dir" "$service_dir" "$state_dir"
install -m 0755 "$script_dir/bridge.py" "$install_dir/bridge.py"
install -m 0644 "$script_dir/systemd/lockscreen-notification-bridge.service" "$service_dir/lockscreen-notification-bridge.service"
systemctl --user daemon-reload
systemctl --user enable --now lockscreen-notification-bridge.service
printf 'Installed and started the user-session service.\n'
printf 'Run: %s --self-test-notifications\n' "$install_dir/bridge.py"
