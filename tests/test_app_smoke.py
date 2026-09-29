# -*- coding: utf-8 -*-
"""整机冒烟测试：
1. 全局热键真实注册 + 模拟按键触发回调
2. 完整应用作为子进程启动 → 常驻存活 → 退出
"""
import ctypes
import os
import subprocess
import sys
import threading
import time
import unittest

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
sys.path.insert(0, ROOT)

PYTHON = sys.executable

# Win32 虚拟键码
VK_CONTROL, VK_MENU, VK_F7 = 0x11, 0x12, 0x76
KEYEVENTF_KEYUP = 0x0002


def _send_ctrl_alt_f7():
    """用 Win32 keybd_event 合成 Ctrl+Alt+F7。

    keyboard 库按扫描码匹配热键，因此必须带上正确的扫描码：
    Ctrl=0x1D, Alt=0x38, F7=0x41。
    """
    u = ctypes.windll.user32
    for vk, scan, flags in [
        (VK_CONTROL, 0x1D, 0), (VK_MENU, 0x38, 0), (VK_F7, 0x41, 0),
        (VK_F7, 0x41, KEYEVENTF_KEYUP), (VK_MENU, 0x38, KEYEVENTF_KEYUP),
        (VK_CONTROL, 0x1D, KEYEVENTF_KEYUP),
    ]:
        u.keybd_event(vk, scan, flags, 0)
        time.sleep(0.05)


class TestHotkeyManager(unittest.TestCase):
    def test_hotkey_register_and_fire(self):
        from screenlens.hotkey import HotkeyManager

        fired = threading.Event()

        def on_fire():
            fired.set()

        mgr = HotkeyManager(on_fire)
        ok, msg = mgr.register("ctrl+alt+f7")
        self.assertTrue(ok, msg)
        try:
            time.sleep(0.5)
            _send_ctrl_alt_f7()
            self.assertTrue(fired.wait(5), "热键回调应在 5 秒内触发")
        finally:
            mgr.shutdown()

    def test_hotkey_unregister(self):
        from screenlens.hotkey import HotkeyManager

        mgr = HotkeyManager(lambda: None)
        ok, _ = mgr.register("ctrl+alt+f8")
        self.assertTrue(ok)
        mgr.unregister()
        self.assertIsNone(mgr._current)


class TestAppLaunch(unittest.TestCase):
    """启动完整应用（含托盘、热键、OCR 预热），验证常驻不崩溃。"""

    def test_app_starts_and_stays_alive(self):
        env = dict(os.environ)
        env["APPDATA"] = os.path.join(ROOT, "tests", "_appdata_tmp")
        os.makedirs(env["APPDATA"], exist_ok=True)

        proc = subprocess.Popen(
            [PYTHON, "-u", os.path.join(ROOT, "run_legacy.py")],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            env=env, cwd=ROOT)
        try:
            # 等待 10 秒，进程应持续存活（常驻后台）
            deadline = time.time() + 10
            while time.time() < deadline:
                if proc.poll() is not None:
                    out = proc.stdout.read().decode("utf-8", "replace")
                    self.fail(f"应用提前退出 (code={proc.returncode}):\n{out}")
                time.sleep(0.5)
            self.assertIsNone(proc.poll(), "10 秒后应用应仍在运行")
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
            out = proc.stdout.read().decode("utf-8", "replace")
            # 不应有未捕获异常
            self.assertNotIn("Traceback", out,
                              f"应用日志中不应有未捕获异常:\n{out}")
        # 进程正常终止（被 terminate 后非 crash 退出码）
        self.assertNotEqual(proc.returncode, -1073741819, "不应发生访问违规崩溃")


if __name__ == "__main__":
    unittest.main()
