"""Current-user login startup; registration is changed only by an explicit toggle."""
from __future__ import annotations

from pathlib import Path
import subprocess
import sys
import winreg

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = "ScreenLens"


def startup_command() -> str:
    if getattr(sys, "frozen", False):
        command = [sys.executable, "--autostart"]
    else:
        interpreter = Path(sys.executable).with_name("pythonw.exe")
        entry = Path(__file__).resolve().parents[2] / "run_agent.pyw"
        if not interpreter.is_file() or not entry.is_file():
            raise OSError("找不到后台启动程序，请确认 Python 环境和项目路径完整。")
        command = [str(interpreter), str(entry), "--autostart"]
    return subprocess.list2cmdline(command)


class StartupRegistration:
    def __init__(self, key_path=RUN_KEY):
        self.key_path = key_path

    def snapshot(self):
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.key_path) as key:
                return winreg.QueryValueEx(key, VALUE_NAME)
        except FileNotFoundError:
            return None

    def enabled(self) -> bool:
        value = self.snapshot()
        return bool(value and value[1] == winreg.REG_SZ and isinstance(value[0], str) and value[0])

    def restore(self, value) -> None:
        if value is None:
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.key_path, 0,
                                    winreg.KEY_SET_VALUE) as key:
                    winreg.DeleteValue(key, VALUE_NAME)
            except FileNotFoundError:
                pass
        else:
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, self.key_path, 0,
                                    winreg.KEY_SET_VALUE) as key:
                winreg.SetValueEx(key, VALUE_NAME, 0, value[1], value[0])

    def set_enabled(self, enabled: bool) -> None:
        self.restore((startup_command(), winreg.REG_SZ) if enabled else None)
