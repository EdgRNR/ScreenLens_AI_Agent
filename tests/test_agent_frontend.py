import os
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from screenlens.agent.frontend import FrontendManager


class _FakeProcess:
    pid = 987654
    returncode = 0

    def poll(self):
        return None

    def wait(self):
        return 0

    def terminate(self):
        pass


class TestFrontendLaunch(unittest.TestCase):
    @patch("screenlens.agent.frontend.subprocess.Popen")
    @patch("screenlens.agent.frontend.find_frontend_exe",
           return_value=r"D:\app\ScreenLens.WinUI.exe")
    def test_capture_mode_maps_to_winui_switch(self, _find, popen):
        popen.return_value = _FakeProcess()
        manager = FrontendManager()

        manager.launch("capture")

        command = popen.call_args.args[0]
        self.assertEqual(command, [r"D:\app\ScreenLens.WinUI.exe", "--capture"])
        self.assertFalse(popen.call_args.kwargs.get("shell", False))

    @patch("screenlens.agent.frontend.subprocess.Popen")
    @patch("screenlens.agent.frontend.find_frontend_exe",
           return_value=r"D:\app\ScreenLens.WinUI.exe")
    def test_settings_mode_maps_to_winui_switch(self, _find, popen):
        popen.return_value = _FakeProcess()
        manager = FrontendManager()

        manager.launch("settings")

        command = popen.call_args.args[0]
        self.assertEqual(command, [r"D:\app\ScreenLens.WinUI.exe", "--settings"])


if __name__ == "__main__":
    unittest.main()
