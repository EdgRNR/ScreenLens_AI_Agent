"""Startup settings use isolated HKCU keys and temporary configuration files."""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch
import uuid

from screenlens.config import Config, validate_config
from screenlens.ipc.protocol import read_json_frame


class ReplyPipe:
    def __init__(self):
        self.output = io.BytesIO()

    def write_all(self, data):
        self.output.write(data)

    def response(self):
        self.output.seek(0)
        return read_json_frame(self.output.read)["data"]


@unittest.skipUnless(os.name == "nt", "Windows login startup")
class StartupTests(unittest.TestCase):
    def setUp(self):
        import winreg
        from screenlens.agent.app import HeadlessAgent
        from screenlens.agent.startup import StartupRegistration

        self.winreg = winreg
        self.key = rf"Software\ScreenLens\Tests\Startup-{uuid.uuid4().hex}"
        self.registration = StartupRegistration(self.key)
        self.addCleanup(self.cleanup_registry)
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.agent = HeadlessAgent.__new__(HeadlessAgent)
        self.agent.config = Config(os.path.join(self.directory.name, "config.json"))
        self.agent.startup = self.registration
        self.agent._shortcut_lock = threading.RLock()
        self.agent._stop = threading.Event()
        self.agent._startup_background = False
        self.agent.frontends = Mock()
        self.agent.workers = Mock()
        self.agent._notify = Mock()

    def cleanup_registry(self):
        try:
            self.winreg.DeleteKey(self.winreg.HKEY_CURRENT_USER, self.key)
        except FileNotFoundError:
            pass

    def dispatch(self, operation, data=None):
        pipe = ReplyPipe()
        self.agent._dispatch(pipe, {"v": 1, "id": 7, "op": operation, "data": data or {}})
        return pipe.response()

    def save(self, enabled, minimized):
        return self.dispatch("SetStartupSettings", {
            "launch_at_startup": enabled, "start_minimized": minimized})

    def test_defaults_enable_disable_reload_and_unrelated_registry_values(self):
        self.assertEqual(self.dispatch("GetStartupSettings"),
                         {"launch_at_startup": False, "start_minimized": True})
        with self.winreg.CreateKey(self.winreg.HKEY_CURRENT_USER, self.key) as key:
            self.winreg.SetValueEx(key, "OtherApp", 0, self.winreg.REG_SZ, "preserve")
        self.assertTrue(self.save(True, False)["launch_at_startup"])
        self.assertIn("--autostart", self.registration.snapshot()[0])
        self.assertFalse(Config(self.agent.config.path).as_dict()["startup"]["start_minimized"])
        self.assertFalse(self.save(False, True)["launch_at_startup"])
        with self.winreg.OpenKey(self.winreg.HKEY_CURRENT_USER, self.key) as key:
            self.assertEqual(self.winreg.QueryValueEx(key, "OtherApp")[0], "preserve")
        self.assertEqual(self.agent.workers.mock_calls, [])

    def test_config_failure_restores_previous_registry_entry_and_configuration(self):
        self.save(True, True)
        previous_entry = self.registration.snapshot()
        previous_config = self.agent.config.as_dict()
        from screenlens.agent.app import IpcError
        with patch.object(self.agent.config, "_atomic_write", side_effect=OSError("disk full")):
            with self.assertRaises(IpcError) as error:
                self.save(False, False)
        self.assertEqual(error.exception.code, "startup_failed")
        self.assertEqual(self.registration.snapshot(), previous_entry)
        self.assertEqual(self.agent.config.as_dict(), previous_config)
        self.assertEqual(Config(self.agent.config.path).as_dict(), previous_config)

    def test_registration_failure_does_not_write_configuration(self):
        from screenlens.agent.app import IpcError
        previous = self.agent.config.as_dict()
        with patch.object(self.registration, "set_enabled", side_effect=PermissionError("denied")):
            with self.assertRaises(IpcError):
                self.save(True, False)
        self.assertEqual(Config(self.agent.config.path).as_dict(), previous)
        self.assertIsNone(self.registration.snapshot())

    def test_failure_restores_absent_entry_and_reports_failed_rollback(self):
        from screenlens.agent.app import IpcError
        with patch.object(self.agent.config, "_atomic_write", side_effect=OSError("disk full")):
            with self.assertRaises(IpcError):
                self.save(True, False)
            self.assertIsNone(self.registration.snapshot())
            with patch.object(self.registration, "restore", side_effect=[None, OSError("denied")]):
                with self.assertRaises(IpcError) as error:
                    self.save(True, False)
            self.assertIn("回退也失败", error.exception.message)

    def test_unknown_configuration_fields_and_read_status_preserved(self):
        config = self.agent.config.as_dict()
        config["future_option"] = {"keep": 3}
        config["startup"]["future_startup_option"] = "keep"
        self.agent.config.apply_dict(config)
        self.save(False, False)
        saved = Config(self.agent.config.path).as_dict()
        self.assertEqual(saved["future_option"], {"keep": 3})
        self.assertEqual(saved["startup"]["future_startup_option"], "keep")
        self.assertEqual(saved["translation"], config["translation"])
        self.assertEqual(saved["hotkey"], config["hotkey"])
        with patch.object(self.registration, "enabled", side_effect=PermissionError("denied")):
            self.assertIn("error", self.dispatch("GetStartupSettings"))

    def test_invalid_input_has_no_side_effects(self):
        from screenlens.agent.app import IpcError
        for data in ({}, {"launch_at_startup": 1, "start_minimized": True},
                     {"launch_at_startup": False, "start_minimized": "false"}):
            with self.assertRaises(IpcError):
                self.dispatch("SetStartupSettings", data)
        self.assertIsNone(self.registration.snapshot())
        self.assertTrue(self.agent._start_minimized())
        self.assertTrue(validate_config({"startup": {"start_minimized": 1}}))

    def test_normal_launch_respects_preference_and_explicit_background_does_not_show_settings(self):
        self.agent._on_server_ready()
        self.agent.frontends.launch.assert_not_called()
        self.assertEqual(self.agent.workers.mock_calls, [])
        self.save(False, False)
        self.agent._on_server_ready()
        self.agent.frontends.launch.assert_called_once_with("settings")
        self.agent.frontends.launch.reset_mock()
        self.agent._startup_background = True
        self.agent._on_server_ready()
        self.agent.frontends.launch.assert_not_called()
        self.agent._startup_background = False
        self.agent._stop.set()
        self.agent._on_server_ready()
        self.agent.frontends.launch.assert_not_called()

    def test_invalid_startup_section_is_safe_and_repairable(self):
        Path(self.agent.config.path).write_text(json.dumps({"startup": None}), encoding="utf-8")
        self.agent.config.load()
        self.assertTrue(self.dispatch("GetStartupSettings")["start_minimized"])
        self.agent._on_server_ready()
        self.agent.frontends.launch.assert_not_called()
        self.save(False, False)
        self.assertFalse(Config(self.agent.config.path).load_error)

    def test_startup_command_quotes_paths_and_packaged_entry(self):
        from screenlens.agent.startup import startup_command
        executable = r"C:\Program Files\ScreenLens\python.exe"
        with patch.object(sys, "frozen", False, create=True), \
                patch.object(sys, "executable", executable), \
                patch.object(Path, "is_file", return_value=True):
            self.assertTrue(startup_command().startswith('"C:\\Program Files\\ScreenLens\\pythonw.exe" '))
        with patch.object(sys, "frozen", True, create=True), patch.object(sys, "executable", executable):
            self.assertEqual(startup_command(), subprocess.list2cmdline([executable, "--autostart"]))
        with patch.object(sys, "frozen", False, create=True), patch.object(Path, "is_file", return_value=False):
            with self.assertRaises(OSError):
                startup_command()

    def test_pythonw_agent_uses_console_python_for_framed_worker(self):
        from screenlens.agent.workers import WorkerManager
        executable = r"C:\Program Files\ScreenLens\pythonw.exe"
        with patch.object(sys, "frozen", False, create=True), \
                patch.object(sys, "executable", executable), patch("os.path.isfile", return_value=True):
            self.assertEqual(WorkerManager._python_executable(),
                             r"C:\Program Files\ScreenLens\python.exe")

    def test_server_ready_runs_once_and_clients_can_connect_during_startup(self):
        from screenlens.ipc.pipe_server import PipeServer

        ready = threading.Event()
        callback = Mock(side_effect=ready.set)
        name = rf"\\.\pipe\ScreenLens.StartupTest.{uuid.uuid4().hex}"
        server = PipeServer(name, lambda pipe: pipe.write_all(pipe.read_exact(1)), on_ready=callback)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            self.assertTrue(ready.wait(3), "IPC did not become ready")
            for _ in range(2):
                deadline = time.monotonic() + 3
                while True:
                    try:
                        connection = open(name, "r+b", buffering=0)
                        break
                    except OSError:
                        if time.monotonic() > deadline:
                            raise
                        time.sleep(0.01)
                with connection:
                    connection.write(b"x")
                    self.assertEqual(connection.read(1), b"x")
            callback.assert_called_once()
        finally:
            server.stop()
            thread.join(3)
        self.assertFalse(thread.is_alive())
