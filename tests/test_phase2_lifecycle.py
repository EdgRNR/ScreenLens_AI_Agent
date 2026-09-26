# -*- coding: utf-8 -*-
"""第二阶段 P0 测试：异步任务生命周期 / 翻译防重复 / 旧结果丢弃 /
主线程调度器。"""
import os
import queue
import sys
import threading
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import tkinter as tk

from PIL import Image

from screenlens.dispatch import MainThreadDispatcher
from screenlens.ocr.engine import OcrResult
from screenlens.translate.provider import TranslationError, TranslationProvider
from screenlens.ui.result_window import ResultWindow

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


class FakeOcrEngine:
    """可控的假 OCR 引擎：按指定延迟返回指定文本。"""

    def __init__(self):
        self.lock = threading.Lock()
        self.jobs = []          # 每个元素 (delay, text)

    def expect(self, delay, text):
        with self.lock:
            self.jobs.append((delay, text))

    def recognize(self, img):
        with self.lock:
            delay, text = self.jobs.pop(0)
        time.sleep(delay)
        if not text:  # 与真实引擎一致：空文本 → 空结果
            return OcrResult([], [])
        return OcrResult([text], [0.99])


class SlowProvider(TranslationProvider):
    name = "slow_test"

    def __init__(self, delay=1.0, result="译文内容"):
        self.delay = delay
        self.result = result
        self.calls = 0

    def translate(self, text, target_lang):
        self.calls += 1
        time.sleep(self.delay)
        return self.result


class FailingProvider(TranslationProvider):
    name = "failing_test"

    def translate(self, text, target_lang):
        raise TranslationError("翻译失败，请检查网络或服务配置。")


class CfgStub:
    def __init__(self, provider="none"):
        self.translation = {"provider": provider, "target_language": "zh"}


@unittest.skipUnless(GUI_AVAILABLE, "无可用桌面会话")
class TestResultWindowLifecycle(unittest.TestCase):
    def setUp(self):
        self.root = tk.Tk()
        self.root.withdraw()
        self.engine = FakeOcrEngine()
        self.recaptured = threading.Event()

    def tearDown(self):
        try:
            self.root.destroy()
        except tk.TclError:
            pass
        import gc

        gc.collect()  # 主线程回收 Tk 对象，防止跨线程 GC 崩溃

    def _make_window(self, provider=None):
        rw = ResultWindow(self.root, self.engine, CfgStub(),
                          on_recapture=self.recaptured.set)
        if provider is not None:
            rw._provider = provider
        return rw

    def _wait_text(self, rw, timeout=10):
        deadline = time.time() + timeout
        while time.time() < deadline:
            pump(self.root, 50)
            if rw._text.get("1.0", "end-1c"):
                return rw._text.get("1.0", "end-1c")
        return ""

    # -------------------------------------------------- 旧任务结果丢弃

    def test_stale_ocr_result_dropped(self):
        """慢 OCR 完成时不得覆盖新一轮任务的结果。"""
        img = Image.new("RGB", (80, 30), "white")
        rw = self._make_window()
        self.engine.expect(delay=1.0, text="旧结果不应出现")
        rw.show_ocr(img, (10, 10, 200, 100))
        # 立刻开始第二轮（更快完成）
        self.engine.expect(delay=0.05, text="新任务结果")
        rw.show_ocr(img, (10, 10, 200, 100))
        text = self._wait_text(rw)
        pump(self.root, 1500)  # 等旧任务迟到返回
        self.assertEqual(text, "新任务结果")
        self.assertEqual(rw._text.get("1.0", "end-1c"), "新任务结果")

    def test_close_window_invalidates_tasks(self):
        """关闭浮窗后，迟到的 OCR 结果不应导致任何 Tk 异常。"""
        img = Image.new("RGB", (80, 30), "white")
        rw = self._make_window()
        self.engine.expect(delay=0.8, text="迟到结果")
        rw.show_ocr(img, (10, 10, 200, 100))
        rw.close()
        pump(self.root, 1500)  # 旧 worker 会把结果放入队列，无 poller 消费
        self.assertIsNone(rw._win)

    # -------------------------------------------------- 翻译防重复

    def test_translate_dedup_while_busy(self):
        provider = SlowProvider(delay=1.0)
        img = Image.new("RGB", (80, 30), "white")
        rw = self._make_window(provider)
        self.engine.expect(delay=0.01, text="Hello world")
        rw.show_ocr(img, (10, 10, 200, 100))
        self.assertIn("Hello", self._wait_text(rw))

        rw._on_translate()
        self.assertTrue(rw._translating)
        rw._on_translate()  # 进行中重复点击
        status = rw._status.cget("text")
        self.assertIn("翻译进行中", status)
        pump(self.root, 1800)
        self.assertEqual(provider.calls, 1)
        self.assertFalse(rw._translating)
        self.assertIn("翻译完成", rw._status.cget("text"))
        self.assertEqual(rw._trans_text.get("1.0", "end-1c"), "译文内容")
        rw.close()

    def test_translate_failure_recovers_button(self):
        img = Image.new("RGB", (80, 30), "white")
        rw = self._make_window(FailingProvider())
        self.engine.expect(delay=0.01, text="Hello")
        rw.show_ocr(img, (10, 10, 200, 100))
        self._wait_text(rw)
        rw._on_translate()
        deadline = time.time() + 5
        while time.time() < deadline and "失败" not in rw._status.cget("text"):
            pump(self.root, 50)
        self.assertIn("翻译失败", rw._status.cget("text"))
        self.assertFalse(rw._translating)  # 按钮状态已恢复
        # 失败后可以再次发起（provider 会再次失败，但流程不卡死）
        rw._on_translate()
        deadline = time.time() + 5
        while time.time() < deadline and "失败" not in rw._status.cget("text"):
            pump(self.root, 50)
        self.assertFalse(rw._translating)
        rw.close()

    def test_translate_placeholder_not_translated(self):
        """OCR 空结果时（占位符"未识别到文字"）不得发起翻译。"""
        provider = SlowProvider(delay=0.1)
        img = Image.new("RGB", (80, 30), "white")
        rw = self._make_window(provider)
        self.engine.expect(delay=0.01, text="")  # 空结果
        rw.show_ocr(img, (10, 10, 200, 100))
        self._wait_text(rw)
        rw._on_translate()
        pump(self.root, 300)
        self.assertEqual(provider.calls, 0)
        self.assertIn("没有可翻译", rw._status.cget("text"))
        rw.close()

    # -------------------------------------------------- 隐私提示

    def test_provider_hint_privacy_text(self):
        rw = self._make_window()
        from screenlens.translate.google_free import GoogleFreeProvider
        from screenlens.translate.openai_compat import OpenAICompatProvider

        hint = rw._provider_hint(GoogleFreeProvider())
        self.assertIn("仅识别文本将发送", hint)
        self.assertIn("截图不会上传", hint)
        self.assertIn("Google", hint)

        oa = OpenAICompatProvider(
            {"base_url": "https://api.deepseek.com/v1", "api_key": "k",
             "model": "m"})
        hint = rw._provider_hint(oa)
        self.assertIn("api.deepseek.com", hint)
        self.assertIn("截图不会上传", hint)

        from screenlens.translate.provider import NoneProvider
        self.assertIsNone(rw._provider_hint(NoneProvider()))

    def test_translate_sends_privacy_notice(self):
        provider = SlowProvider(delay=0.1, result="好")
        img = Image.new("RGB", (80, 30), "white")
        rw = self._make_window(provider)
        self.engine.expect(delay=0.01, text="Hello")
        rw.show_ocr(img, (10, 10, 200, 100))
        self._wait_text(rw)
        rw._on_translate()
        status = rw._status.cget("text")
        self.assertIn("翻译中", status)
        self.assertIn("截图不会上传", status)
        pump(self.root, 500)
        rw.close()


class TestMainThreadDispatcher(unittest.TestCase):
    @unittest.skipUnless(GUI_AVAILABLE, "无可用桌面会话")
    def test_post_runs_on_main_thread(self):
        root = tk.Tk()
        root.withdraw()
        d = MainThreadDispatcher(root)
        d.start()
        executed = threading.Event()
        main_thread_ident = root.after(0, lambda: None)  # warm up

        def check():
            self.assertEqual(threading.main_thread().ident,
                             threading.current_thread().ident)
            executed.set()

        # 从工作线程投递
        threading.Thread(
            target=lambda: d.post(check), daemon=True).start()
        deadline = time.time() + 5
        while time.time() < deadline and not executed.is_set():
            root.update()
            time.sleep(0.01)
        self.assertTrue(executed.is_set())
        d.stop()
        root.destroy()

    def test_post_after_stop_returns_false(self):
        d = MainThreadDispatcher(None)
        called = []
        self.assertFalse(d.post(lambda: called.append(1)))
        self.assertEqual(called, [])

    @unittest.skipUnless(GUI_AVAILABLE, "无可用桌面会话")
    def test_stop_prevents_execution_after_destroy(self):
        root = tk.Tk()
        root.withdraw()
        d = MainThreadDispatcher(root)
        d.start()
        d.stop()
        executed = []
        d.post(lambda: executed.append(1))
        self.assertEqual(executed, [])
        root.destroy()


if __name__ == "__main__":
    unittest.main()
