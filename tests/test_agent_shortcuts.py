"""Shortcut edits must route actions, preserve settings, and roll back failures."""
import io
import os
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch

from screenlens.config import Config, DEFAULT_CONFIG
from screenlens.hotkey import ShortcutRegistry
from screenlens.ipc.protocol import read_json_frame


@unittest.skipUnless(os.name == "nt", "Windows keyboard mappings")
class TestKeyboardEventDispatch(unittest.TestCase):
    """Use the library's real dispatch pipeline, without installing OS hooks."""

    def setUp(self):
        import keyboard
        from screenlens.agent.app import HeadlessAgent

        self.keyboard = keyboard
        self.listener = keyboard._KeyboardListener()
        self.listener.init()
        self.listener.start_if_necessary = lambda: None
        for name, value in (("_listener", self.listener), ("_pressed_events", {}), ("_hotkeys", {})):
            replacement = patch.object(keyboard, name, value)
            replacement.start()
            self.addCleanup(replacement.stop)

        self.agent = HeadlessAgent.__new__(HeadlessAgent)
        self.agent._stop = threading.Event()
        self.agent._recording_until = 0.0
        self.agent._hotkey_mutex = threading.Lock()
        self.agent._last_hotkey_launch = 0.0
        self.agent.frontends = Mock()
        self.registry = ShortcutRegistry({
            action: lambda action=action: self.agent._trigger_shortcut(action)
            for action in ("capture", *DEFAULT_CONFIG["hotkeys"])
        })
        self.addCleanup(self.registry.shutdown)
        self.keys = {"capture": "ctrl+`", "capture_translate": "ctrl+alt+t",
                     "capture_ocr": "f9", "settings": "ctrl+alt+s", "cancel_capture": "ctrl+alt+q"}
        self.assertTrue(self.registry.apply(self.keys)[0])

    def press(self, combination):
        codes = [group[0] for group in self.keyboard.parse_hotkey(combination)[0]]
        for event_type, code in ([('down', code) for code in codes]
                                 + [('up', code) for code in reversed(codes)]):
            event = self.keyboard.KeyboardEvent(event_type, code)
            self.listener.direct_callback(event)
            self.listener.pre_process_event(self.listener.queue.get_nowait())

    def test_actual_key_events_launch_every_configured_action(self):
        for action, expected in (
            ("capture", ("capture", "--capture-action=capture")),
            ("capture_translate", ("capture", "--capture-action=translate")),
            ("capture_ocr", ("capture", "--capture-action=ocr")),
            ("settings", ("settings",)),
            ("cancel_capture", ("cancel-capture",)),
        ):
            with self.subTest(action=action):
                self.agent._last_hotkey_launch = 0.0
                self.agent.frontends.launch.reset_mock()
                self.press(self.keys[action])
                self.agent.frontends.launch.assert_called_once_with(*expected)

    def test_recording_then_cancel_restores_actual_dispatch(self):
        self.agent._dispatch(ReplyPipe(), {"v": 1, "op": "SetHotkeyRecording", "id": 1,
                                          "data": {"active": True}})
        self.press(self.keys["capture"])
        self.agent.frontends.launch.assert_not_called()
        self.agent._dispatch(ReplyPipe(), {"v": 1, "op": "SetHotkeyRecording", "id": 2,
                                          "data": {"active": False}})
        self.press(self.keys["capture"])
        self.agent.frontends.launch.assert_called_once_with("capture", "--capture-action=capture")

    def test_rebind_removes_old_gesture_and_shutdown_removes_new(self):
        self.assertTrue(self.registry.apply({**self.keys, "capture": "alt+4"})[0])
        self.press(self.keys["capture"])
        self.agent.frontends.launch.assert_not_called()
        self.press("alt+4")
        self.agent.frontends.launch.assert_called_once_with("capture", "--capture-action=capture")
        self.agent.frontends.launch.reset_mock()
        self.agent._last_hotkey_launch = 0.0
        self.registry.shutdown()
        self.press("alt+4")
        self.agent.frontends.launch.assert_not_called()


class ReplyPipe:
    def __init__(self):
        self.output = io.BytesIO()

    def write_all(self, data):
        self.output.write(data)

    def response(self):
        self.output.seek(0)
        return read_json_frame(self.output.read)


@unittest.skipUnless(os.name == "nt", "Windows keyboard mappings")
class TestShortcutRegistry(unittest.TestCase):
    def setUp(self):
        self.callbacks = {action: Mock() for action in ("capture", *DEFAULT_CONFIG["hotkeys"])}
        self.registry = ShortcutRegistry(self.callbacks)
        self.keys = {"capture": "ctrl+alt+a", **DEFAULT_CONFIG["hotkeys"]}
        self.add = patch("keyboard.add_hotkey", side_effect=lambda *a, **kw: Mock()).start()
        self.remove = patch("keyboard.remove_hotkey").start()
        self.addCleanup(patch.stopall)

    def test_local_escape_and_unset_shortcuts_do_not_install_global_hooks(self):
        self.assertTrue(self.registry.apply(self.keys)[0])
        self.assertEqual(self.add.call_count, 1)
        self.add.call_args.args[1]()
        self.callbacks["capture"].assert_called_once()
        # Reapplying settings reuses the registration rather than accumulating hooks.
        self.assertTrue(self.registry.apply(self.keys)[0])
        self.assertEqual(self.add.call_count, 1)

    def test_alias_duplicate_rejected_without_removing_old_registration(self):
        self.registry.apply(self.keys)
        ok, message = self.registry.apply({**self.keys, "capture_translate": "control+alt+a"})
        self.assertFalse(ok)
        self.assertIn("重复", message)
        self.assertEqual(self.add.call_count, 1)
        self.remove.assert_not_called()

    def test_failed_registration_rolls_back_all_staged_hooks(self):
        self.registry.apply(self.keys)
        old = self.registry._managers["capture"]
        staged_handle = Mock()
        self.add.side_effect = [staged_handle, RuntimeError("registration failed")]
        ok, _ = self.registry.apply({**self.keys, "capture": "ctrl+alt+b",
                                      "capture_translate": "ctrl+alt+t"})
        self.assertFalse(ok)
        self.assertIs(self.registry._managers["capture"], old)
        self.remove.assert_called_once_with(staged_handle)

    def test_swapping_actions_and_alias_edit_reuse_hooks_without_stale_callbacks(self):
        self.registry.apply({**self.keys, "capture_translate": "ctrl+alt+t"})
        capture_hook = self.registry._managers["capture"]
        translation_hook = self.registry._managers["capture_translate"]
        self.registry.apply({**self.keys, "capture": "ctrl+alt+t", "capture_translate": "control+alt+a"})
        self.assertIs(self.registry._managers["capture"], translation_hook)
        self.assertIs(self.registry._managers["capture_translate"], capture_hook)
        capture_hook._fire()
        self.callbacks["capture_translate"].assert_called_once()
        self.callbacks["capture"].assert_not_called()
        self.assertEqual(self.add.call_count, 2)
        self.remove.assert_not_called()

    def test_clear_and_shutdown_remove_exact_handles(self):
        self.registry.apply({**self.keys, "settings": "f9"})
        handle = self.registry._managers["settings"]._handle
        self.registry.apply(self.keys)
        self.remove.assert_called_once_with(handle)
        self.registry.shutdown()
        self.assertEqual(self.remove.call_count, 2)


@unittest.skipUnless(os.name == "nt", "Windows agent IPC")
class TestAgentShortcuts(unittest.TestCase):
    def setUp(self):
        from screenlens.agent.app import HeadlessAgent

        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.agent = HeadlessAgent.__new__(HeadlessAgent)
        self.agent.config = Config(os.path.join(self.directory.name, "config.json"))
        self.agent._shortcut_lock = threading.RLock()
        self.agent._stop = threading.Event()
        self.agent._recording_until = 0.0
        self.agent.hotkeys = Mock()
        self.agent.hotkeys.apply.return_value = (True, "")
        self.agent._launch_capture = Mock()
        self.agent.frontends = Mock()

    def dispatch(self, op, data):
        pipe = ReplyPipe()
        self.agent._dispatch(pipe, {"v": 1, "op": op, "id": 7, "data": data})
        return pipe.response()["data"]

    def test_edit_reload_and_reset_all_shortcuts_preserve_translation(self):
        original_translation = self.agent.config.translation
        for action, key in (("capture_translate", "ctrl+alt+t"), ("capture_ocr", "f9"),
                            ("settings", "ctrl+alt+s"), ("cancel_capture", "ctrl+alt+q")):
            response = self.dispatch("RegisterHotkey", {"action": action, "hotkey": key})
            self.assertEqual(response["config"]["hotkeys"][action], key)
        reloaded = Config(self.agent.config.path)
        self.assertEqual(reloaded.as_dict()["hotkeys"]["capture_ocr"], "f9")
        self.dispatch("RegisterHotkey", {"action": "capture_ocr", "hotkey": ""})
        response = self.dispatch("ResetHotkeys", {})
        self.assertEqual(response["config"]["hotkey"], "ctrl+`")
        self.assertEqual(response["config"]["hotkeys"], DEFAULT_CONFIG["hotkeys"])
        self.assertEqual(response["config"]["translation"], original_translation)

    def test_save_failure_restores_memory_disk_and_registrations(self):
        from screenlens.agent.app import IpcError

        previous = self.agent.config.as_dict()
        with patch.object(self.agent.config, "_atomic_write", side_effect=OSError("disk failure")):
            with self.assertRaises(IpcError):
                self.dispatch("RegisterHotkey", {"action": "capture_translate", "hotkey": "f9"})
        self.assertEqual(self.agent.config.as_dict(), previous)
        self.assertEqual(Config(self.agent.config.path).as_dict(), previous)
        self.assertEqual(self.agent.hotkeys.apply.call_count, 2)
        self.assertEqual(self.agent.hotkeys.apply.call_args.args[0]["capture_translate"], "")

    def test_translation_save_does_not_reset_shortcuts(self):
        self.dispatch("RegisterHotkey", {"action": "capture_translate", "hotkey": "f9"})
        self.dispatch("SaveSettings", {"config": {"translation": {"provider": "none"}}})
        self.assertEqual(self.agent.config.as_dict()["hotkeys"]["capture_translate"], "f9")

    def test_distinct_actions_and_recording_suppression(self):
        for action, mode in (("capture", "capture"), ("capture_translate", "translate"), ("capture_ocr", "ocr")):
            self.agent._trigger_shortcut(action)
            self.agent._launch_capture.assert_called_with(mode)
        self.agent._trigger_shortcut("settings")
        self.agent.frontends.launch.assert_called_with("settings")
        self.agent._trigger_shortcut("cancel_capture")
        self.agent.frontends.launch.assert_called_with("cancel-capture")
        self.agent._launch_capture.reset_mock()
        self.dispatch("SetHotkeyRecording", {"active": True})
        self.agent._trigger_shortcut("capture")
        self.agent._launch_capture.assert_not_called()
        self.agent._recording_until = time.monotonic() - 1
        self.agent._trigger_shortcut("capture")
        self.agent._launch_capture.assert_called_once_with("capture")


@unittest.skipUnless(os.name == "nt", "Windows frontend")
class TestFrontendActionForwarding(unittest.TestCase):
    def test_active_capture_accepts_new_action_and_cancel_command(self):
        from screenlens.agent.frontend import FrontendManager

        manager = FrontendManager()
        manager._exe = "existing.exe"
        manager._procs[1] = {"proc": Mock(), "mode": "capture"}
        manager._procs[1]["proc"].poll.return_value = None
        with patch("screenlens.agent.frontend.os.path.isfile", return_value=True), \
                patch("screenlens.agent.frontend.subprocess.Popen") as spawn, \
                patch("screenlens.agent.frontend.threading.Thread"):
            manager.launch("capture", "--capture-action=translate")
            self.assertEqual(spawn.call_args.args[0], ["existing.exe", "--capture", "--capture-action=translate"])
            manager.launch("cancel-capture")
            self.assertEqual(spawn.call_args.args[0], ["existing.exe", "--cancel-capture"])
