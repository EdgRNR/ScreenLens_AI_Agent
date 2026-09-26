# -*- coding: utf-8 -*-
"""GUI 集成测试：模拟真实鼠标事件，走完 截图 → OCR → 结果浮窗 全链路。

需要真实桌面会话（Windows 交互环境）。
"""
import os
import sys
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import tkinter as tk

from PIL import Image

from screenlens.capture import screen as screen_util
from screenlens.capture.overlay import CaptureOverlay
from screenlens.ocr.engine import OcrEngine
from screenlens.ui.result_window import ResultWindow

GUI_AVAILABLE = True
try:
    tk.Tk().destroy()
except Exception:
    GUI_AVAILABLE = False


def pump(root, ms=50):
    """处理 tkinter 事件循环 ms 毫秒。"""
    end = time.time() + ms / 1000
    while time.time() < end:
        root.update()
        time.sleep(0.01)


@unittest.skipUnless(GUI_AVAILABLE, "无可用桌面会话")
class TestOverlayGui(unittest.TestCase):
    def setUp(self):
        self.root = tk.Tk()
        self.root.withdraw()
        self.captured = None
        self.cancelled = None

    def tearDown(self):
        try:
            self.root.destroy()
        except tk.TclError:
            pass
        import gc

        gc.collect()  # 主线程回收 Tk 对象，防止跨线程 GC 崩溃

    def _make_overlay(self):
        return CaptureOverlay(self.root, self._on_captured, self._on_cancel)

    def _on_captured(self, crop, bbox):
        self.captured = (crop, bbox)

    def _on_cancel(self, reason):
        self.cancelled = reason

    def _show_overlay(self, overlay):
        overlay.start()
        pump(self.root, 300)  # 等待 150ms 延迟 + 冻结画面
        self.assertIsNotNone(overlay._win, "遮罩窗口应已创建")

    def _sim_rect(self, overlay, x0, y0, x1, y1):
        c = overlay._canvas
        c.event_generate("<ButtonPress-1>", x=x0, y=y0)
        pump(self.root, 30)
        c.event_generate("<B1-Motion>", x=(x0 + x1) // 2, y=(y0 + y1) // 2)
        pump(self.root, 30)
        c.event_generate("<ButtonRelease-1>", x=x1, y=y1)
        pump(self.root, 30)

    def test_rect_capture(self):
        overlay = self._make_overlay()
        self._show_overlay(overlay)
        self._sim_rect(overlay, 100, 100, 300, 200)
        self.assertIsNotNone(self.captured, "矩形选区应触发 on_captured")
        crop, bbox = self.captured
        self.assertIsInstance(crop, Image.Image)
        self.assertEqual(crop.size, (200 + 12, 100 + 12))  # +padding 6*2
        self.assertEqual(bbox, (100, 100, 300, 200))       # 屏幕坐标

    def test_tiny_click_is_ignored(self):
        overlay = self._make_overlay()
        self._show_overlay(overlay)
        self._sim_rect(overlay, 100, 100, 103, 104)  # 太小
        self.assertIsNone(self.captured)
        self.assertIsNone(self.cancelled)
        self.assertFalse(overlay._finished)

    def test_freeform_capture(self):
        overlay = self._make_overlay()
        self._show_overlay(overlay)
        c = overlay._canvas
        cx, cy, r = 200, 200, 60
        import math

        pts = [(int(cx + r * math.cos(a / 16 * 2 * math.pi)),
                int(cy + r * math.sin(a / 16 * 2 * math.pi)))
               for a in range(16)]
        c.event_generate("<ButtonPress-3>", x=pts[0][0], y=pts[0][1])
        pump(self.root, 20)
        for i, (x, y) in enumerate(pts[1:], 1):
            c.event_generate("<B3-Motion>", x=x, y=y)
            pump(self.root, 10)
        c.event_generate("<ButtonRelease-3>", x=pts[0][0], y=pts[0][1])
        pump(self.root, 30)
        self.assertIsNotNone(self.captured, "自由圈选应触发 on_captured")
        crop, bbox = self.captured
        self.assertIsInstance(crop, Image.Image)
        w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
        self.assertAlmostEqual(crop.width, w + 12, delta=2)
        self.assertAlmostEqual(crop.height, h + 12, delta=2)

    def test_escape_cancels(self):
        overlay = self._make_overlay()
        self._show_overlay(overlay)
        overlay._win.event_generate("<Escape>")
        pump(self.root, 50)
        self.assertIsNotNone(self.cancelled)
        self.assertIsNone(self.captured)


@unittest.skipUnless(GUI_AVAILABLE, "无可用桌面会话")
class TestResultWindowGui(unittest.TestCase):
    """端到端：真实屏幕截图 → OCR → 结果浮窗显示。"""

    @classmethod
    def setUpClass(cls):
        cls.engine = OcrEngine()
        cls.engine.warmup_async()
        cls.engine._ensure_engine()

    def setUp(self):
        self.root = tk.Tk()
        self.root.withdraw()

        class CfgStub:
            translation = {"provider": "none", "target_language": "zh"}

        self.cfg = CfgStub()
        self.recapture_called = False

    def tearDown(self):
        try:
            self.root.destroy()
        except tk.TclError:
            pass
        import gc

        gc.collect()  # 主线程回收 Tk 对象，防止跨线程 GC 崩溃

    def _on_recapture(self):
        self.recapture_called = True

    def test_show_ocr_from_generated_image(self):
        """用生成的文字图替代真实屏幕（更可控），走 ResultWindow 全流程。"""
        from tests.test_ocr import make_test_image

        path = make_test_image()
        with Image.open(path) as img:
            crop = img.copy()
        rw = ResultWindow(self.root, self.engine, self.cfg,
                          on_recapture=self._on_recapture)
        rw.show_ocr(crop, (100, 100, 700, 400))
        deadline = time.time() + 20
        text = ""
        while time.time() < deadline:
            pump(self.root, 100)
            text = rw._text.get("1.0", "end-1c")
            if "Hello" in text or "未识别" in text:
                break
        self.assertIn("Hello", text)
        self.assertIn("今天天气不错", text.replace("\n", ""))
        rw.close()

    def test_empty_ocr_shows_hint(self):
        rw = ResultWindow(self.root, self.engine, self.cfg,
                          on_recapture=self._on_recapture)
        rw.show_ocr(Image.new("RGB", (300, 150), "white"),
                    (100, 100, 400, 250))
        deadline = time.time() + 20
        text = ""
        while time.time() < deadline:
            pump(self.root, 100)
            text = rw._text.get("1.0", "end-1c")
            if text:
                break
        self.assertEqual(text, "未识别到文字")
        rw.close()

    def test_translate_not_configured(self):
        rw = ResultWindow(self.root, self.engine, self.cfg,
                          on_recapture=self._on_recapture)
        rw.show_ocr(Image.new("RGB", (100, 50), "white"),
                    (100, 100, 200, 150))
        # 先等 OCR 流程结束（文本框出现"未识别到文字"占位）
        deadline = time.time() + 20
        while time.time() < deadline:
            pump(self.root, 100)
            if rw._text.get("1.0", "end-1c"):
                break
        # 模拟已有识别文字（provider=none 场景关注的是提示文案）
        rw._has_text = True
        rw._text.delete("1.0", "end")
        rw._text.insert("1.0", "Hello world")
        # provider=none：应提示翻译未启用并引导到设置，而非声称联网
        rw._on_translate()
        pump(self.root, 200)
        status = rw._status.cget("text")
        self.assertIn("翻译未启用", status)
        self.assertIn("设置", status)
        self.assertNotIn("发送", status)
        rw.close()


if __name__ == "__main__":
    unittest.main()
