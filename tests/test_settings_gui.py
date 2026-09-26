# -*- coding: utf-8 -*-
"""设置窗口测试：保存 / 取消 / 校验 / 热键录入解析。"""
import os
import sys
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import tkinter as tk

from screenlens.config import Config
from screenlens.ui.settings_window import SettingsWindow, hotkey_from_event

GUI_AVAILABLE = True
try:
    tk.Tk().destroy()
except Exception:
    GUI_AVAILABLE = False


def pump(root, ms=50):
    end = time.time() + ms / 1000
    while time.time() < end:
        root.update()
        time.sleep(0.01)


class FakeEvent:
    def __init__(self, keysym, state=0):
        self.keysym = keysym
        self.state = state


class TestHotkeyFromEvent(unittest.TestCase):
    CTRL_ALT = 0x0004 | 0x0008
    CTRL = 0x0004
    SHIFT_CTRL = 0x0001 | 0x0004

    def test_ctrl_alt_letter(self):
        hk, err = hotkey_from_event(FakeEvent("a", self.CTRL_ALT))
        self.assertIsNone(err)
        self.assertEqual(hk, "ctrl+alt+a")

    def test_shift_ctrl_letter(self):
        hk, err = hotkey_from_event(FakeEvent("s", self.SHIFT_CTRL))
        self.assertIsNone(err)
        self.assertEqual(hk, "ctrl+shift+s")

    def test_lone_function_key_allowed(self):
        hk, err = hotkey_from_event(FakeEvent("f9", 0))
        self.assertIsNone(err)
        self.assertEqual(hk, "f9")

    def test_lone_letter_rejected(self):
        hk, err = hotkey_from_event(FakeEvent("f", 0))  # 普通字母 f
        self.assertIsNone(hk)
        self.assertIn("普通按键", err)

    def test_lone_digit_rejected(self):
        hk, err = hotkey_from_event(FakeEvent("5", 0))
        self.assertIsNone(hk)
        self.assertIn("普通按键", err)

    def test_modifier_only_rejected(self):
        hk, err = hotkey_from_event(FakeEvent("control_l", self.CTRL))
        self.assertIsNone(hk)
        self.assertIsNotNone(err)

    def test_escape_cancels(self):
        hk, err = hotkey_from_event(FakeEvent("Escape", 0))
        self.assertIsNone(hk)
        self.assertIsNone(err)

    def test_print_screen_alone(self):
        hk, err = hotkey_from_event(FakeEvent("print_screen", 0))
        self.assertIsNone(err)
        self.assertEqual(hk, "print_screen")


@unittest.skipUnless(GUI_AVAILABLE, "无可用桌面会话")
class TestSettingsWindow(unittest.TestCase):
    def setUp(self):
        import tempfile

        from screenlens.ui import settings_window as sw_module

        fd, self.path = tempfile.mkstemp(suffix=".json")
        os.close(fd)
        os.remove(self.path)
        self.config = Config(self.path)
        self.root = tk.Tk()
        self.root.withdraw()
        self.applied = []
        # 替换模态 messagebox 为记录桩，避免测试被弹窗阻塞
        self._sw = sw_module
        self._orig_mb = sw_module.messagebox
        self.mb_calls = []

        class StubMessageBox:
            def showinfo(inner, *a, **k):
                self.mb_calls.append(("info",) + a)

            def showerror(inner, *a, **k):
                self.mb_calls.append(("error",) + a)

            def showwarning(inner, *a, **k):
                self.mb_calls.append(("warning",) + a)

        sw_module.messagebox = StubMessageBox()

    def tearDown(self):
        self._sw.messagebox = self._orig_mb
        try:
            self.root.destroy()
        except tk.TclError:
            pass
        import gc

        gc.collect()  # 主线程回收 Tk 对象，防止跨线程 GC 崩溃
        if os.path.exists(self.path):
            os.remove(self.path)

    def _apply_ok(self, hotkey, translation):
        self.applied.append((hotkey, translation))
        self.config.hotkey = hotkey
        self.config.set_translation(translation)
        self.config.save()
        return []

    def _apply_hotkey_fail(self, hotkey, translation):
        self.applied.append((hotkey, translation))
        return ["快捷键 ctrl+alt+q 注册失败：测试模拟（已保留原快捷键）"]

    def _make_window(self, apply_cb):
        win = SettingsWindow(self.root, self.config, apply_cb)
        win.show()
        pump(self.root, 100)
        return win

    def test_show_and_close(self):
        win = self._make_window(self._apply_ok)
        self.assertTrue(win.is_shown())
        win._close()
        self.assertFalse(win.is_shown())

    def test_save_applies_settings(self):
        win = self._make_window(self._apply_ok)
        # 修改字段
        win._hotkey_var.set("ctrl+alt+s")
        win._provider_var.set("none")
        win._lang_var.set("英文")
        win._on_save()
        pump(self.root, 100)

        self.assertEqual(len(self.applied), 1)
        hotkey, translation = self.applied[0]
        self.assertEqual(hotkey, "ctrl+alt+s")
        self.assertEqual(translation["provider"], "none")
        self.assertEqual(translation["target_language"], "en")
        # 配置真实写盘
        cfg2 = Config(self.path)
        self.assertEqual(cfg2.hotkey, "ctrl+alt+s")
        self.assertEqual(cfg2.translation["provider"], "none")
        win._close()

    def test_cancel_does_not_apply(self):
        win = self._make_window(self._apply_ok)
        win._hotkey_var.set("ctrl+alt+x")
        win._lang_var.set("日文")
        win._close()
        self.assertEqual(self.applied, [])
        cfg2 = Config(self.path)
        self.assertEqual(cfg2.hotkey, "ctrl+alt+a")  # 未保存

    def test_invalid_url_blocked_before_apply(self):
        win = self._make_window(self._apply_ok)
        win._provider_var.set("openai")
        win._url_var.set("not-a-url")
        win._on_save()
        pump(self.root, 100)
        # 结构校验失败：不应调用 apply
        self.assertEqual(self.applied, [])
        win._close()

    def test_hotkey_failure_rolls_back_display(self):
        """热键注册失败：apply 返回错误，界面回显当前生效值。"""
        win = SettingsWindow(self.root, self.config, self._apply_hotkey_fail)
        win.show()
        pump(self.root, 100)
        win._hotkey_var.set("ctrl+alt+q")
        win._provider_var.set("google_free")
        win._on_save()
        pump(self.root, 100)
        # 界面回显旧的（仍然生效的）快捷键
        self.assertEqual(win._hotkey_var.get(), "ctrl+alt+a")
        win._close()

    def test_reset_defaults_fills_ui(self):
        win = self._make_window(self._apply_ok)
        win._hotkey_var.set("ctrl+alt+z")
        win._provider_var.set("none")
        win._on_reset_defaults()
        pump(self.root, 50)
        self.assertEqual(win._hotkey_var.get(), "ctrl+alt+a")
        self.assertEqual(win._provider_var.get(), "google_free")
        self.assertEqual(self.applied, [])  # 恢复默认不等于保存
        win._close()

    def test_capture_key_updates_hotkey_var(self):
        win = self._make_window(self._apply_ok)
        win._start_capture()
        self.assertTrue(win._capturing)
        event = FakeEvent("j", 0x0004 | 0x0008)
        win._on_capture_key(event)
        self.assertEqual(win._hotkey_var.get(), "ctrl+alt+j")
        self.assertFalse(win._capturing)
        win._close()

    def test_api_key_hidden_by_default(self):
        win = self._make_window(self._apply_ok)
        self.assertEqual(str(win._key_entry.cget("show")), "*")
        win._close()


if __name__ == "__main__":
    unittest.main()
