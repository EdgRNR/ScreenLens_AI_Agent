"""Native worker routing, process ownership and actual protocol recovery."""
import io
import os
from pathlib import Path
import struct
import subprocess
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from PIL import Image

from screenlens.agent.ocr_worker import MODEL_FILES, WORKER_FILES, native_ocr_command
from screenlens.agent.workers import WorkerError, WorkerManager, _WorkerProc
from tests.test_agent_cancellation import _FakeWorker
from tests.test_translation_api import ApiFixture
from http.server import ThreadingHTTPServer

ROOT = Path(__file__).resolve().parent.parent


class TestNativeRouting(unittest.TestCase):
    def test_backend_switch_disposes_old_worker_and_reuses_same_backend(self):
        manager = WorkerManager()
        native, translation, second_native = (_FakeWorker() for _ in range(3))
        with patch("screenlens.agent.workers.native_ocr_command", return_value=["native.exe", "models"]), \
                patch.object(manager, "_spawn", side_effect=[native, translation, second_native]) as spawn, \
                patch.object(manager, "_schedule_idle"):
            try:
                manager.run_ocr(b"image")
                manager.run_ocr(b"image")
                self.assertEqual(spawn.call_count, 1)
                manager.run_translate("Hello", {}, "zh")
                native.dispose.assert_called_once()
                self.assertEqual(manager._worker_backend, "python")
                manager.run_ocr(b"image")
                translation.dispose.assert_called_once()
                self.assertEqual(manager._worker_backend, "csharp-ocr")
                self.assertEqual(spawn.call_count, 3)
            finally:
                manager.dispose()

    def test_missing_native_keeps_python_available_and_strict_mode_reports_error(self):
        for backend in ("auto", "csharp"):
            manager = WorkerManager()
            with patch.dict(os.environ, {"SCREENLENS_OCR_BACKEND": backend}), \
                    patch("screenlens.agent.workers.native_ocr_command", return_value=None), \
                    patch.object(manager, "_spawn", return_value=_FakeWorker()) as spawn, \
                    patch.object(manager, "_schedule_idle"):
                try:
                    if backend == "auto":
                        manager.run_ocr(b"image")
                        self.assertEqual(manager._worker_backend, "python")
                    else:
                        with self.assertRaises(WorkerError) as error:
                            manager.run_ocr(b"image")
                        self.assertEqual(error.exception.code, "ocr_failed")
                        spawn.assert_not_called()
                finally:
                    manager.dispose()

    def test_stale_idle_callback_does_not_kill_active_request(self):
        manager = WorkerManager()
        worker = _FakeWorker()
        manager._worker = worker
        with manager._lock:
            manager._idle_exit()
        self.assertIs(manager._worker, worker)
        worker.dispose.assert_not_called()
        manager.dispose()

    def test_missing_models_do_not_launch_partially_packaged_worker(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory, \
                patch.dict(os.environ, {"SCREENLENS_OCR_BACKEND": "auto"}), \
                patch("screenlens.agent.ocr_worker.importlib.util.find_spec", return_value=None):
            output = Path(directory) / "dist/ocr"
            output.mkdir(parents=True)
            for name in WORKER_FILES:
                (output / name).touch()
            self.assertIsNone(native_ocr_command(directory))
            (output / "models").mkdir()
            for name in MODEL_FILES:
                (output / "models" / name).touch()
            self.assertEqual(native_ocr_command(directory), [
                str(output / "ScreenLens.Ocr.Worker.exe"), str(output / "models")])

    def test_queued_request_is_not_cancelled_by_previous_active_request(self):
        manager = WorkerManager()
        manager._lock.acquire()
        manager._current_cancelled.set()
        started = threading.Event()
        def queued():
            started.set()
            return manager.run_ocr(b"image")
        with patch.object(manager, "_spawn", return_value=_FakeWorker()), \
                patch.object(manager, "_schedule_idle"), ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(queued)
            try:
                self.assertTrue(started.wait(2))
                # Force the waiter to inspect cancellation before it acquires
                # the task lock; active cancellation belongs to the old task.
                import time
                time.sleep(.2)
            finally:
                manager._lock.release()
            try:
                self.assertEqual(future.result(timeout=3), {"text": "next capture"})
            finally:
                manager.dispose()


@unittest.skipUnless(os.name == "nt" and native_ocr_command(str(ROOT)),
                     "Build C# worker with scripts/build_ocr_worker.py first")
class TestNativeWorkerProtocol(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(os.environ, {"SCREENLENS_OCR_BACKEND": "csharp"})
        self.env.start()
        self.manager = WorkerManager()

    def tearDown(self):
        proc = self.manager._worker.proc if self.manager._worker else None
        self.manager.dispose()
        self.env.stop()
        if proc:
            self.assertIsNotNone(proc.poll(), "worker must exit on dispose")

    @staticmethod
    def blank(size=(64, 32)):
        stream = io.BytesIO()
        Image.new("RGB", size, "white").save(stream, format="PNG")
        return stream.getvalue()

    def test_invalid_image_recovers_and_empty_small_large_images_are_valid(self):
        with self.assertRaises(WorkerError) as error:
            self.manager.run_ocr(b"not PNG")
        self.assertEqual(error.exception.code, "ocr_failed")
        pid = self.manager._worker.proc.pid
        for size in ((64, 32), (1, 1), (3500, 100), (100, 3500), (2000, 8)):
            with self.subTest(size=size):
                result = self.manager.run_ocr(self.blank(size))
                self.assertEqual(result["text"], "")
                self.assertEqual(result["lines"], [])
                self.assertGreaterEqual(result["elapsed_ms"], 0)
                self.assertEqual(self.manager._worker.proc.pid, pid)

    def test_actual_models_match_python_fixture_and_return_bounded_boxes(self):
        png = (ROOT / "tests/data/ocr_test.png").read_bytes()
        native = self.manager.run_ocr(png)
        with patch.dict(os.environ, {"SCREENLENS_OCR_BACKEND": "python"}):
            python = self.manager.run_ocr(png)
        self.assertEqual(native["text"], python["text"])
        self.assertTrue(native["lines"])
        with Image.open(io.BytesIO(png)) as img:
            for line in native["lines"]:
                self.assertGreaterEqual(line["score"], .5)
                self.assertEqual(len(line["box"]), 4)
                for x, y in line["box"]:
                    self.assertTrue(0 <= x <= img.width and 0 <= y <= img.height)

    def test_cancel_during_cold_request_returns_cancelled_then_restarts(self):
        entered = threading.Event()
        original = _WorkerProc.read_json
        def reading(worker):
            entered.set()
            return original(worker)
        with patch.object(_WorkerProc, "read_json", reading), ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(self.manager.run_ocr, self.blank())
            self.assertTrue(entered.wait(10))
            proc = self.manager._worker.proc
            self.manager.cancel_current()
            with self.assertRaises(WorkerError) as error:
                future.result(timeout=10)
            self.assertEqual(error.exception.code, "cancelled")
            proc.wait(timeout=5)
            self.assertEqual(self.manager.run_ocr(self.blank())["text"], "")
            self.assertNotEqual(proc.pid, self.manager._worker.proc.pid)

    def test_ocr_streaming_translation_and_ocr_switch_use_one_worker(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), ApiFixture)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            self.manager.run_ocr(self.blank())
            ocr_proc = self.manager._worker.proc
            chunks = []
            cfg = {"provider": "openai", "openai": {
                "base_url": f"http://127.0.0.1:{server.server_port}/v1",
                "model": "stream", "api_key": "local-test-key"}}
            result = self.manager.run_translate("Hello", cfg, "zh", on_delta=chunks.append)
            self.assertEqual(result["text"], "你好，世界！")
            self.assertEqual("".join(chunks), result["text"])
            self.assertIsNotNone(ocr_proc.poll())
            translation_proc = self.manager._worker.proc
            self.assertEqual(self.manager.run_ocr(self.blank())["text"], "")
            self.assertIsNotNone(translation_proc.poll())
        finally:
            server.shutdown()
            server.server_close()
            thread.join(2)

    def test_oversized_or_truncated_frame_exits_without_hanging(self):
        command = native_ocr_command(str(ROOT))
        for raw in (struct.pack(">I", 33 * 1024 * 1024), b"\x00\x00"):
            with self.subTest(raw=raw):
                proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
                try:
                    stdout, _ = proc.communicate(raw, timeout=5)
                    self.assertNotEqual(proc.returncode, 0)
                    self.assertEqual(stdout, b"")
                finally:
                    if proc.poll() is None:
                        proc.kill()
                        proc.wait(5)
