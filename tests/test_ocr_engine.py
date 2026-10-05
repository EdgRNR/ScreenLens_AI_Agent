"""Shared engine initialization, recovery and concurrent inference contracts."""
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
from unittest.mock import Mock, patch

from PIL import Image

from screenlens.ocr.engine import OcrEngine, OcrError


def output(text="中文 Hello"):
    return SimpleNamespace(txts=[text], scores=[0.98], boxes=[
        [[1, 2], [30, 2], [30, 20], [1, 20]]])


class TestSharedOcrEngine(unittest.TestCase):
    def test_loads_once_and_preserves_original_image_box_coordinates(self):
        backend = Mock(return_value=output())
        with patch("rapidocr.RapidOCR", return_value=backend) as factory:
            engine = OcrEngine()
            for _ in range(2):
                result = engine.recognize(Image.new("RGBA", (64, 32), "white"))
                self.assertEqual(result.text, "中文 Hello")
                self.assertEqual(result.boxes, [[[1, 2], [30, 2], [30, 20], [1, 20]]])
        factory.assert_called_once()
        self.assertEqual(backend.call_count, 2)

    def test_initialization_failure_can_retry_in_retained_worker(self):
        backend = Mock(return_value=output())
        engine = OcrEngine()
        with patch("rapidocr.RapidOCR", side_effect=[RuntimeError("temporary failure"), backend]), \
                patch("screenlens.ocr.engine.logger"):
            with self.assertRaisesRegex(OcrError, "temporary failure"):
                engine.recognize(Image.new("RGB", (64, 32)))
            self.assertEqual(engine.recognize(Image.new("RGB", (64, 32))).text, "中文 Hello")

    def test_shared_backend_never_runs_concurrently(self):
        first_started = threading.Event()
        second_started = threading.Event()
        release_first = threading.Event()
        calls = []

        def infer(arr):
            calls.append(len(calls))
            if len(calls) == 1:
                first_started.set()
                if not release_first.wait(3):
                    raise AssertionError("first inference was not released")
            else:
                second_started.set()
            return output()

        engine = OcrEngine()
        engine._ocr = infer
        with ThreadPoolExecutor(max_workers=2) as pool:
            first = pool.submit(engine.recognize, Image.new("RGB", (64, 32)))
            self.assertTrue(first_started.wait(2))
            second = pool.submit(engine.recognize, Image.new("RGB", (64, 32)))
            try:
                self.assertFalse(second_started.wait(0.1))
            finally:
                release_first.set()
            self.assertEqual(first.result(timeout=2).text, "中文 Hello")
            self.assertEqual(second.result(timeout=2).text, "中文 Hello")
        self.assertEqual(len(calls), 2)


if __name__ == "__main__":
    unittest.main()
