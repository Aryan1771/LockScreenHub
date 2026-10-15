import importlib.util
import json
import os
import sys
import tempfile
import unittest
from datetime import datetime, timedelta
from pathlib import Path
import time
from unittest.mock import patch

MODULE_PATH = Path(__file__).resolve().parents[1] / "bridge.py"
SPEC = importlib.util.spec_from_file_location("bridge", MODULE_PATH)
bridge = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = bridge
SPEC.loader.exec_module(bridge)


class NotificationTests(unittest.TestCase):
    def test_download_upload_and_updates_replace_existing_card(self):
        calls = []

        def runner(command, **kwargs):
            calls.append(command)
            return type("Result", (), {"stdout": f"(uint32 {100 + len(calls)},)", "stderr": ""})()

        notifier = bridge.DbusNotifier(runner)
        for direction in ("Download", "Upload"):
            transfer = bridge.Transfer(direction.lower(), direction, "test.bin", "Starting", 10, 100)
            bridge.render_transfer(notifier, transfer)
            bridge.render_transfer(notifier, bridge.Transfer(direction.lower(), direction, "test.bin", "Progress changed", 60, 100))
            bridge.render_transfer(notifier, bridge.Transfer(direction.lower(), direction, "test.bin", "Complete", 100, 100, False))

        self.assertEqual(len(calls), 6)
        # The second update and completion for each transfer pass its prior D-Bus ID.
        self.assertEqual(calls[1][10], "101")
        self.assertEqual(calls[2][10], "102")
        self.assertIn("Upload: test.bin — 60%", " ".join(calls[4]))
        # A fresh service instance starts without IDs from the previous notification daemon session.
        bridge.DbusNotifier(runner).send("upload", "Upload activity", "Still updating")
        self.assertEqual(calls[-1][10], "0")

    def test_self_test_generates_six_scenarios(self):
        class FakeNotifier:
            def __init__(self):
                self.events = []

            def send(self, key, title, body, progress=None):
                self.events.append((key, title, body, progress))
                return len(self.events)

        notifier = FakeNotifier()
        bridge.Bridge(notifier).self_test()
        self.assertEqual(len(notifier.events), 6)
        self.assertEqual(notifier.events[0][3], 10)
        self.assertEqual(notifier.events[1][3], 60)
        self.assertIn("Complete", notifier.events[2][2])
        self.assertIn("Upload:", notifier.events[3][2])

    def test_duplicate_snapshot_is_suppressed_and_state_change_is_sent(self):
        class FakeNotifier:
            def __init__(self): self.events = []
            def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
        notifier = FakeNotifier()
        app = bridge.Bridge(notifier)
        active = bridge.Transfer("steam-1", "Download", "Game", "Downloading", 10, 100)
        app.publish(active)
        app.publish(active)
        app.publish(bridge.Transfer("steam-1", "Download", "Game", "Downloading", 20, 100))
        app.publish(bridge.Transfer("steam-1", "Download", "Game", "Paused", 20, 100, False))
        self.assertEqual(len(notifier.events), 3)
        self.assertIn("Paused", notifier.events[-1][2])


class BrowserTests(unittest.TestCase):
    def test_partial_download_scan_tracks_count_and_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "one.crdownload").write_bytes(b"12345")
            (root / "two.part").write_bytes(b"123")
            (root / "finished.zip").write_bytes(b"ignored")
            activity = bridge.read_browser_activity({"Chrome": {root}})
            self.assertEqual(activity, {"Chrome": (2, 8)})

    def test_custom_chromium_download_folder_is_discovered(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            profile = home / ".config/google-chrome/Default"
            target = home / "Saved files"
            profile.mkdir(parents=True)
            target.mkdir()
            (profile / "Preferences").write_text(json.dumps({"download": {"default_directory": str(target)}}))
            self.assertIn(target, bridge.discover_browser_dirs(home)["Chrome"])

    def test_completed_partial_file_disappears_from_activity(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            partial = root / "finished.zip.crdownload"
            partial.write_bytes(b"download bytes")
            self.assertEqual(bridge.read_browser_activity({"Chrome": {root}})["Chrome"][0], 1)
            partial.rename(root / "finished.zip")
            self.assertEqual(bridge.read_browser_activity({"Chrome": {root}}), {})

    def test_old_partial_file_on_startup_is_seeded_without_alert(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            root = home / "Downloads"
            root.mkdir()
            partial = root / "old.crdownload"
            partial.write_bytes(b"old")
            old_time = time.time() - 3600
            os.utime(partial, (old_time, old_time))
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            with patch.object(bridge, "steam_roots", return_value=[]), patch.object(bridge, "discover_browser_dirs", return_value={"Chrome": {root}}), patch.object(bridge, "process_running", return_value=True):
                bridge.Bridge(notifier, home).scan_once()
            self.assertEqual(notifier.events, [])

    def test_new_download_and_completion_each_update_card(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            root = home / "Downloads"
            root.mkdir()
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            app = bridge.Bridge(notifier, home)
            with patch.object(bridge, "steam_roots", return_value=[]), patch.object(bridge, "discover_browser_dirs", return_value={"Chrome": {root}}), patch.object(bridge, "process_running", return_value=True):
                stale = root / "old.crdownload"
                stale.write_bytes(b"old")
                old_time = time.time() - 3600
                os.utime(stale, (old_time, old_time))
                app.scan_once()
                stale.unlink()
                app.scan_once()
                partial = root / "new.crdownload"
                partial.write_bytes(b"x")
                app.scan_once()
                partial.unlink()
                app.scan_once()
            self.assertEqual(len(notifier.events), 2)
            self.assertIn("1 active file", notifier.events[0][2])
            self.assertIn("No longer active", notifier.events[1][2])

    def test_browser_progress_accumulates_small_writes_before_replacing_card(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            root = home / "Downloads"
            root.mkdir()
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            app = bridge.Bridge(notifier, home)
            with patch.object(bridge, "steam_roots", return_value=[]), patch.object(bridge, "discover_browser_dirs", return_value={"Chrome": {root}}), patch.object(bridge, "process_running", return_value=True):
                app.scan_once()
                partial = root / "large.crdownload"
                partial.write_bytes(b"x")
                app.scan_once()
                with partial.open("ab") as output:
                    output.write(b"a" * (1024 * 1024))
                app.scan_once()
                self.assertEqual(len(notifier.events), 1)
                with partial.open("ab") as output:
                    output.write(b"b" * (4 * 1024 * 1024))
                app.scan_once()
            self.assertEqual(len(notifier.events), 2)
            self.assertIn("Download activity", notifier.events[1][1])
            self.assertEqual(notifier.events[0][0], notifier.events[1][0])


class SteamTests(unittest.TestCase):
    def _steam_fixture(self, home: Path, state_time: datetime):
        steam = home / ".local/share/Steam"
        apps = steam / "steamapps"
        logs = steam / "logs"
        apps.mkdir(parents=True)
        logs.mkdir()
        (apps / "appmanifest_1659040.acf").write_text('"AppState" { "appid" "1659040" "name" "Hitman" "BytesDownloaded" "40" "BytesToDownload" "100" }')
        stamp = state_time.strftime("%Y-%m-%d %H:%M:%S")
        (logs / "content_log.txt").write_text(
            f'[{stamp}] AppID 1659040 App update changed : Running Update,Downloading,\n'
            f'[{stamp}] AppID 1659040 update started: download 50/100\n'
        )

    def test_stale_manifest_and_old_log_do_not_create_download_card(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            start = datetime.now() - timedelta(minutes=1)
            self._steam_fixture(home, start - timedelta(hours=4))
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            with patch.object(bridge, "steam_process_started", return_value=start), patch.object(bridge, "discover_browser_dirs", return_value={}), patch.object(bridge, "process_running", return_value=False):
                bridge.Bridge(notifier, home).scan_once()
            self.assertEqual(notifier.events, [])

    def test_current_session_active_state_creates_download_card(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            start = datetime.now() - timedelta(minutes=1)
            self._steam_fixture(home, start + timedelta(seconds=5))
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            with patch.object(bridge, "steam_process_started", return_value=start), patch.object(bridge, "discover_browser_dirs", return_value={}), patch.object(bridge, "process_running", return_value=False):
                bridge.Bridge(notifier, home).scan_once()
            self.assertEqual(len(notifier.events), 1)
            self.assertIn("Hitman", notifier.events[0][2])
            self.assertIn("50%", notifier.events[0][2])

    def test_paused_state_does_not_create_an_active_download(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            start = datetime.now() - timedelta(minutes=1)
            self._steam_fixture(home, start + timedelta(seconds=5))
            log = home / ".local/share/Steam/logs/content_log.txt"
            stamp = (start + timedelta(seconds=10)).strftime("%Y-%m-%d %H:%M:%S")
            with log.open("a") as out:
                out.write(f'[{stamp}] AppID 1659040 App update changed : Update Paused,\n')
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            with patch.object(bridge, "steam_process_started", return_value=start), patch.object(bridge, "discover_browser_dirs", return_value={}), patch.object(bridge, "process_running", return_value=False):
                bridge.Bridge(notifier, home).scan_once()
            self.assertEqual(notifier.events, [])

    def test_complete_manifest_is_not_reported_as_active(self):
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            start = datetime.now() - timedelta(minutes=1)
            self._steam_fixture(home, start + timedelta(seconds=5))
            manifest = home / ".local/share/Steam/steamapps/appmanifest_1659040.acf"
            manifest.write_text(manifest.read_text().replace('"BytesDownloaded" "40"', '"BytesDownloaded" "100"'))
            class FakeNotifier:
                def __init__(self): self.events = []
                def send(self, *args, **kwargs): self.events.append(args); return len(self.events)
            notifier = FakeNotifier()
            with patch.object(bridge, "steam_process_started", return_value=start), patch.object(bridge, "discover_browser_dirs", return_value={}), patch.object(bridge, "process_running", return_value=False):
                bridge.Bridge(notifier, home).scan_once()
            self.assertEqual(notifier.events, [])

    def test_steam_log_tracker_reads_appended_pause_state_incrementally(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "Steam"
            logs = root / "logs"
            logs.mkdir(parents=True)
            log = logs / "content_log.txt"
            stamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            log.write_text(f'[{stamp}] AppID 10 App update changed : Running Update,Downloading,\n')
            tracker = bridge.SteamLogTracker()
            self.assertTrue(tracker.read([root])["10"][0])
            offset = tracker.offsets[str(log)]
            with log.open("a") as output:
                output.write(f'[{stamp}] AppID 10 App update changed : Update Paused,\n')
            self.assertFalse(tracker.read([root])["10"][0])
            self.assertGreater(tracker.offsets[str(log)], offset)
            self.assertFalse(tracker.read([root])["10"][0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
