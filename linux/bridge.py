#!/usr/bin/env python3
"""Session-scoped Steam and browser download notifications for Linux desktops."""

from __future__ import annotations

import argparse
import json
import os
import re
import signal
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Callable

APP_NAME = "LockScreen Notification Bridge"
PARTIAL_SUFFIXES = {".crdownload", ".part", ".download", ".opdownload"}
STATE_HOME = Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state")) / "lockscreen-notification-bridge"
LOG_PATH = STATE_HOME / "bridge.log"


@dataclass(frozen=True)
class Transfer:
    key: str
    direction: str
    name: str
    status: str
    done: int = 0
    total: int = 0
    active: bool = True

    def body(self) -> str:
        if not self.active:
            return f"{self.name}: {self.status}"
        if self.total > 0:
            percent = min(100, max(0, round(self.done * 100 / self.total)))
            return f"{self.direction}: {self.name} — {percent}% ({format_bytes(self.done)} / {format_bytes(self.total)}) · {self.status}"
        return f"{self.direction}: {self.name} — {format_bytes(self.done)} observed · {self.status}"


def format_bytes(value: int) -> str:
    amount = float(max(0, value))
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if amount < 1024 or unit == "TiB":
            return f"{amount:.1f} {unit}"
        amount /= 1024
    return f"{amount:.1f} TiB"


class DbusNotifier:
    """Freedesktop notification client; stable IDs update cards in place."""

    def __init__(self, runner: Callable = subprocess.run):
        self.runner = runner
        self.ids: dict[str, int] = {}

    def send(self, key: str, title: str, body: str, progress: int | None = None) -> int:
        replaces_id = int(self.ids.get(key, 0))
        hints = "{'urgency': <byte 0>, 'x-canonical-private-synchronous': <'" + key + "'>"
        if progress is not None:
            hints += ", 'value': <int32 " + str(min(100, max(0, progress))) + ">"
        hints += "}"
        command = [
            "gdbus", "call", "--session", "--dest", "org.freedesktop.Notifications",
            "--object-path", "/org/freedesktop/Notifications", "--method",
            "org.freedesktop.Notifications.Notify", APP_NAME, str(replaces_id), "", title, body, "[]", hints, "0",
        ]
        result = self.runner(command, check=True, capture_output=True, text=True, timeout=8)
        match = re.search(r"uint32\s+(\d+)", result.stdout)
        if not match:
            raise RuntimeError(f"Notification daemon returned no ID: {result.stdout.strip()}")
        notification_id = int(match.group(1))
        self.ids[key] = notification_id
        return notification_id


def render_transfer(notifier: DbusNotifier, transfer: Transfer) -> int:
    title = f"{transfer.direction} activity" if transfer.active else "Transfer activity"
    percent = round(transfer.done * 100 / transfer.total) if transfer.active and transfer.total else None
    return notifier.send(transfer.key, title, transfer.body(), percent)


def discover_browser_dirs(home: Path | None = None) -> dict[str, set[Path]]:
    home = home or Path.home()
    found: dict[str, set[Path]] = {"Browser download": set()}
    common = home / "Downloads"
    if common.is_dir():
        found["Browser download"].add(common)
    chromium = {
        "Chrome": home / ".config/google-chrome",
        "Chromium": home / ".config/chromium",
        "Edge": home / ".config/microsoft-edge",
        "Brave": home / ".config/BraveSoftware/Brave-Browser",
        "Vivaldi": home / ".config/vivaldi",
        "Opera": home / ".config/opera",
    }
    for label, root in chromium.items():
        if not root.is_dir():
            continue
        found.setdefault(label, set())
        for profile in root.iterdir():
            pref = profile / "Preferences"
            try:
                settings = json.loads(pref.read_text(encoding="utf-8"))
                target = settings.get("download", {}).get("default_directory")
                if target and Path(target).is_dir() and Path(target).expanduser() != common:
                    found[label].add(Path(target).expanduser())
            except (OSError, json.JSONDecodeError, AttributeError):
                pass
        if common.is_dir():
            found[label].add(common)

    firefox = home / ".mozilla/firefox"
    profiles_file = firefox / "profiles.ini"
    if profiles_file.is_file():
        found.setdefault("Firefox", set())
        current: dict[str, str] = {}
        sections: list[dict[str, str]] = []
        try:
            for line in profiles_file.read_text(encoding="utf-8").splitlines() + [""]:
                line = line.strip()
                if line.startswith("["):
                    if current:
                        sections.append(current)
                    current = {}
                elif "=" in line:
                    key, value = line.split("=", 1)
                    current[key] = value
            for section in sections:
                value = section.get("Path")
                if not value:
                    continue
                profile = Path(value) if section.get("IsRelative") == "0" else firefox / value
                custom = None
                prefs = profile / "prefs.js"
                if prefs.is_file():
                    match = re.search(r'user_pref\("browser\.download\.dir",\s*"((?:\\.|[^"\\])*)"\)', prefs.read_text(encoding="utf-8"))
                    if match:
                        custom = bytes(match.group(1), "utf-8").decode("unicode_escape")
                target = Path(custom).expanduser() if custom else common
                if target.is_dir() and target != common:
                    found["Firefox"].add(target)
        except OSError:
            pass
    return {label: folders for label, folders in found.items() if folders}


def read_browser_activity(directories: dict[str, set[Path]]) -> dict[str, tuple[int, int]]:
    activity: dict[str, tuple[int, int]] = {}
    for label, roots in directories.items():
        count = size = 0
        for root in roots:
            try:
                for entry in root.iterdir():
                    if entry.suffix.lower() not in PARTIAL_SUFFIXES:
                        continue
                    try:
                        stat = entry.stat()
                        count += 1
                        size += max(0, stat.st_size)
                    except OSError:
                        continue
            except OSError:
                continue
        if count:
            activity[label] = count, size
    return activity


def browser_activity_is_recent(directories: dict[str, set[Path]], seconds: int = 30) -> bool:
    cutoff = time.time() - seconds
    for roots in directories.values():
        for root in roots:
            try:
                for entry in root.iterdir():
                    if entry.suffix.lower() in PARTIAL_SUFFIXES and entry.stat().st_mtime >= cutoff:
                        return True
            except OSError:
                continue
    return False


def steam_roots(home: Path | None = None) -> list[Path]:
    home = home or Path.home()
    roots = {Path(os.environ["STEAM_DIR"]).expanduser()} if os.environ.get("STEAM_DIR") else set()
    roots.update({home / ".local/share/Steam", home / ".steam/steam", home / ".var/app/com.valvesoftware.Steam/.local/share/Steam"})
    for root in list(roots):
        vdf = root / "steamapps/libraryfolders.vdf"
        try:
            for path in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8")):
                roots.add(Path(path.replace("\\\\", "\\")).expanduser())
        except OSError:
            pass
    return sorted(root for root in roots if root.is_dir())


def read_steam_manifests(roots: list[Path]) -> dict[str, tuple[str, int, int]]:
    output = {}
    for root in roots:
        apps = root / "steamapps"
        try:
            manifests = apps.glob("appmanifest_*.acf")
            for manifest in manifests:
                try:
                    text = manifest.read_text(encoding="utf-8", errors="replace")
                except OSError:
                    continue
                def field(name: str) -> str | None:
                    match = re.search(r'"' + re.escape(name) + r'"\s+"([^"]*)"', text, re.I)
                    return match.group(1) if match else None
                app_id = field("appid") or manifest.stem.removeprefix("appmanifest_")
                name = field("name") or "Steam download"
                done, total = field("BytesDownloaded"), field("BytesToDownload")
                if done and total and done.isdigit() and total.isdigit():
                    output[app_id] = name, int(done), int(total)
        except OSError:
            continue
    return output


def steam_process_started() -> datetime | None:
    try:
        boot_time = int(next(line.split()[1] for line in Path("/proc/stat").read_text().splitlines() if line.startswith("btime ")))
        ticks = os.sysconf("SC_CLK_TCK")
        found = []
        for proc in Path("/proc").iterdir():
            if not proc.name.isdigit():
                continue
            try:
                if (proc / "comm").read_text().strip().lower() != "steam":
                    continue
                stat = (proc / "stat").read_text()
                end_name = stat.rfind(")")
                fields = stat[end_name + 2:].split()
                start_ticks = int(fields[19])
                found.append(datetime.fromtimestamp(boot_time + start_ticks / ticks))
            except (OSError, ValueError, IndexError):
                continue
        return max(found) if found else None
    except (OSError, StopIteration, ValueError):
        return None


class SteamLogTracker:
    """Incrementally reads changed Steam log bytes instead of rescanning them every poll."""

    state_rx = re.compile(r"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\].*?AppID\s+(\d+)\s+App update changed\s*:\s*(.*)", re.I)
    progress_rx = re.compile(r"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\].*?AppID\s+(\d+)\s+update started\s*:\s*download\s+(\d+)/(\d+)", re.I)

    def __init__(self):
        self.offsets: dict[str, int] = {}
        self.mtimes: dict[str, int] = {}
        self.by_path: dict[str, dict[str, tuple[bool, datetime, int, int]]] = {}

    def read(self, roots: list[Path]) -> dict[str, tuple[bool, datetime, int, int]]:
        for root in roots:
            path = root / "logs/content_log.txt"
            key = str(path)
            try:
                stat = path.stat()
                offset = self.offsets.get(key, 0)
                if stat.st_size < offset or (stat.st_size == offset and self.mtimes.get(key) not in (None, stat.st_mtime_ns)):
                    offset = 0
                    self.by_path[key] = {}
                state = self.by_path.setdefault(key, {})
                with path.open("rb") as source:
                    source.seek(offset)
                    chunk = source.read()
                lines = chunk.splitlines(keepends=True)
                if lines and not lines[-1].endswith((b"\n", b"\r")):
                    lines.pop()
                consumed = sum(len(line) for line in lines)
                for raw in lines:
                    self._parse_line(raw.decode("utf-8", errors="replace"), state)
                self.offsets[key] = offset + consumed
                self.mtimes[key] = stat.st_mtime_ns
            except OSError:
                continue

        merged: dict[str, tuple[bool, datetime, int, int]] = {}
        for values in self.by_path.values():
            for app_id, value in values.items():
                if app_id not in merged or value[1] >= merged[app_id][1]:
                    merged[app_id] = value
        return merged

    def _parse_line(self, line: str, state: dict[str, tuple[bool, datetime, int, int]]) -> None:
        state_match = self.state_rx.search(line)
        if state_match:
            when = datetime.strptime(state_match.group(1), "%Y-%m-%d %H:%M:%S")
            app = state_match.group(2)
            old = state.get(app, (False, when, 0, 0))
            detail = state_match.group(3)
            state[app] = ("Running Update" in detail and "Downloading" in detail, when, old[2], old[3])
        progress_match = self.progress_rx.search(line)
        if progress_match:
            when = datetime.strptime(progress_match.group(1), "%Y-%m-%d %H:%M:%S")
            app = progress_match.group(2)
            old = state.get(app, (False, when, 0, 0))
            state[app] = (old[0], old[1], int(progress_match.group(3)), int(progress_match.group(4)))


def process_running(candidates: set[str]) -> bool:
    try:
        for proc in Path("/proc").iterdir():
            if not proc.name.isdigit():
                continue
            try:
                cmd = (proc / "comm").read_text().strip().lower()
                if cmd in candidates:
                    return True
            except OSError:
                continue
    except OSError:
        return False
    return False


class Bridge:
    def __init__(self, notifier: DbusNotifier, home: Path | None = None):
        self.notifier = notifier
        self.home = home or Path.home()
        self.last: dict[str, Transfer] = {}
        self.last_scan: dict[str, object] = {}
        self.steam_log_tracker = SteamLogTracker()
        self.browser_initialized = False
        self.browser_baseline: dict[str, tuple[int, int]] = {}
        self.browser_notified = False

    def publish(self, transfer: Transfer) -> int:
        old = self.last.get(transfer.key)
        if old == transfer:
            return 0
        result = render_transfer(self.notifier, transfer)
        self.last[transfer.key] = transfer
        return result

    def scan_once(self) -> None:
        now = datetime.now()
        current: dict[str, Transfer] = {}
        roots = steam_roots(self.home)
        started = steam_process_started()
        manifests = read_steam_manifests(roots)
        states = self.steam_log_tracker.read(roots)
        if started:
            for app_id, (name, done, total) in manifests.items():
                state = states.get(app_id)
                # State timestamps are local Steam log time; require a current-session event.
                if state and state[0] and state[1] >= started and total > done:
                    done = max(done, state[2])
                    total = max(total, state[3])
                    current[f"steam-{app_id}"] = Transfer(f"steam-{app_id}", "Download", name, "Downloading", done, total)

        browser_dirs = discover_browser_dirs(self.home)
        browser_activity = read_browser_activity(browser_dirs)
        browser_processes = {
            "Chrome": {"chrome", "chrome-sandbox"}, "Chromium": {"chromium", "chromium-browse"},
            "Edge": {"msedge"}, "Brave": {"brave", "brave-browser"}, "Vivaldi": {"vivaldi"},
            "Opera": {"opera"}, "Firefox": {"firefox"}, "Browser download": {"chrome", "chromium", "firefox", "brave", "msedge", "vivaldi", "opera"},
        }
        labels = sorted(browser_activity)
        total_files = sum(browser_activity[label][0] for label in labels)
        total_bytes = sum(browser_activity[label][1] for label in labels)
        browser_running = total_files > 0 and any(process_running(browser_processes.get(label, set())) for label in labels)
        if not self.browser_initialized:
            self.browser_initialized = True
            self.browser_baseline = browser_activity.copy()
            if browser_running and browser_activity_is_recent(browser_dirs):
                self.browser_notified = True
                names = ", ".join(labels)
                current["browser-downloads"] = Transfer("browser-downloads", "Download", names, f"{total_files} active file(s)", total_bytes, 0)
        elif browser_running:
            baseline = self.browser_baseline
            materially_changed = browser_activity.keys() != baseline.keys() or any(
                label not in baseline or browser_activity[label][0] != baseline[label][0] or abs(browser_activity[label][1] - baseline[label][1]) >= 5 * 1024 * 1024
                for label in browser_activity
            )
            if materially_changed:
                names = ", ".join(labels)
                current["browser-downloads"] = Transfer("browser-downloads", "Download", names, f"{total_files} active file(s)", total_bytes, 0)
                self.browser_notified = True
                self.browser_baseline = browser_activity.copy()
            elif self.browser_notified and "browser-downloads" in self.last:
                # Keep the active notification state while byte changes accumulate to the update threshold.
                current["browser-downloads"] = self.last["browser-downloads"]
        elif self.browser_notified:
            current["browser-downloads"] = Transfer("browser-downloads", "Download", "Browser download", "No longer active", 0, 0, False)
            self.browser_notified = False
            self.browser_baseline = {}
        else:
            self.browser_baseline = browser_activity.copy()

        for key, transfer in current.items():
            self.publish(transfer)
        for key, old in list(self.last.items()):
            if key not in current and old.active:
                self.publish(Transfer(key, old.direction, old.name, "No longer active", old.done, old.total, False))
        self.last_scan = {"steam_roots": roots, "browser_folders": browser_dirs, "steam_running": bool(started), "time": now}

    def self_test(self) -> None:
        for direction in ("Download", "Upload"):
            key = "self-test-" + direction.lower()
            self.publish(Transfer(key, direction, "SIMULATED (self-test)", "Starting", 1, 10))
            self.publish(Transfer(key, direction, "SIMULATED (self-test)", "Progress changed", 6, 10))
            self.publish(Transfer(key, direction, "SIMULATED (self-test)", "Complete", 10, 10, False))


def log(message: str) -> None:
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as output:
            output.write(f"{datetime.now().isoformat(timespec='seconds')} {message}\n")
    except OSError:
        pass


def main() -> int:
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument("--self-test-notifications", action="store_true", help="show simulated download and upload notifications")
    parser.add_argument("--interval", type=float, default=10, help="polling interval in seconds (default: 10)")
    args = parser.parse_args()
    if args.interval < 2:
        parser.error("--interval must be at least 2 seconds")
    bridge = Bridge(DbusNotifier())
    if args.self_test_notifications:
        bridge.self_test()
        print("Sent simulated download and upload create/update/complete notifications.")
        return 0
    running = True
    def stop(_signal: int, _frame: object) -> None:
        nonlocal running
        running = False
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    log("Bridge started")
    while running:
        try:
            bridge.scan_once()
        except Exception as exc:
            log(f"Scan error: {type(exc).__name__}: {exc}")
        time.sleep(args.interval)
    log("Bridge stopped")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as exc:
        log(f"Notification bus call failed: {exc.stderr.strip() if exc.stderr else exc}")
        print("Could not reach the desktop notification service. Check that you are in a graphical user session with a notification daemon.", file=sys.stderr)
        raise SystemExit(1)
