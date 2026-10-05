"""Local API fixtures exercise SSE, fallbacks, model discovery and worker framing."""
import json
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import Mock, patch

from screenlens.agent.workers import WorkerError, WorkerManager
from screenlens.translate.openai_compat import OpenAICompatProvider
from screenlens.translate.provider import TranslationError
from tests.test_agent_cancellation import _FakeWorker


class ApiFixture(BaseHTTPRequestHandler):
    calls = []
    chunk_delay = 0.04

    def log_message(self, *args):
        pass

    def json_reply(self, data, status=200):
        raw = json.dumps(data).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        self.calls.append((self.path, None))
        self.json_reply({"data": [{"id": "z-model"}, {"id": "a-model"}, {"id": "a-model"}, {"id": None}]})

    def do_POST(self):
        data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.calls.append((self.path, data))
        model = data["model"]
        if model == "unauthorized":
            return self.json_reply({"error": {"message": "secret should not appear"}}, 401)
        if model == "no-stream" and data.get("stream"):
            return self.json_reply({"error": {"param": "stream", "message": "stream not supported"}}, 400)
        if model == "other-error":
            return self.json_reply({"error": {"param": "model", "message": "model not found"}}, 400)
        if not data.get("stream") or model == "json-response":
            return self.json_reply({"choices": [{"message": {"content": "你好，世界！"}}]})
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        try:
            for text in ("你好", "，", "世界", "！"):
                event = {"choices": [{"index": 0, "delta": {"content": text}, "finish_reason": None}]}
                self.wfile.write(("data: " + json.dumps(event, ensure_ascii=False) + "\n\n").encode())
                self.wfile.flush()
                time.sleep(self.chunk_delay)
            if model != "truncated":
                self.wfile.write(b"data: [DONE]\n\n")
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass


class TestTranslationApi(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), ApiFixture)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base_url = f"http://127.0.0.1:{cls.server.server_port}/v1"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(2)

    def setUp(self):
        ApiFixture.calls.clear()

    def provider(self, model="stream"):
        return OpenAICompatProvider({"base_url": self.base_url, "api_key": "local-test-key", "model": model})

    def test_sse_unicode_arrives_incrementally(self):
        chunks = []
        result = self.provider().translate_stream("Hello", "zh", chunks.append)
        self.assertEqual(result, "你好，世界！")
        self.assertEqual(chunks, ["你好", "，", "世界", "！"])
        self.assertEqual(len(ApiFixture.calls), 1)

    def test_explicit_stream_rejection_retries_once_without_stream(self):
        chunks = []
        self.assertEqual(self.provider("no-stream").translate_stream("Hello", "zh", chunks.append), "你好，世界！")
        self.assertEqual(chunks, ["你好，世界！"])
        self.assertEqual([bool(c[1].get("stream")) for c in ApiFixture.calls], [True, False])

    def test_non_stream_json_is_consumed_without_second_request(self):
        chunks = []
        self.provider("json-response").translate_stream("Hello", "zh", chunks.append)
        self.assertEqual(chunks, ["你好，世界！"])
        self.assertEqual(len(ApiFixture.calls), 1)

    def test_authentication_and_model_errors_never_retry_or_leak_body(self):
        for model in ("unauthorized", "other-error"):
            ApiFixture.calls.clear()
            with self.assertRaises(TranslationError) as raised:
                self.provider(model).translate_stream("Hello", "zh", Mock())
            self.assertNotIn("secret", str(raised.exception))
            self.assertEqual(len(ApiFixture.calls), 1)

    def test_truncated_stream_preserves_chunks_and_does_not_retry(self):
        chunks = []
        with self.assertRaisesRegex(TranslationError, "不完整"):
            self.provider("truncated").translate_stream("Hello", "zh", chunks.append)
        self.assertEqual("".join(chunks), "你好，世界！")
        self.assertEqual(len(ApiFixture.calls), 1)

    def test_models_are_deduplicated_and_use_base_path(self):
        self.assertEqual(self.provider().list_models(), ["a-model", "z-model"])
        self.assertEqual(ApiFixture.calls[0][0], "/v1/models")

    def test_real_worker_forwards_progress_then_reuses_framing_for_setup(self):
        manager = WorkerManager()
        chunks = []
        cfg = {"provider": "openai", "openai": {"base_url": self.base_url, "api_key": "local-test-key", "model": "stream"}}
        try:
            result = manager.run_translate("Hello", cfg, "zh", on_delta=chunks.append, request_key="translation")
            self.assertEqual(result, {"text": "你好，世界！"})
            self.assertGreater(len(chunks), 1)
            self.assertEqual("".join(chunks), result["text"])
            models = manager.translation_setup("models", {"openai": cfg["openai"]})
            self.assertEqual(models["models"], ["a-model", "z-model"])
        finally:
            manager.dispose()

    def test_disconnected_progress_discards_worker_before_next_task(self):
        manager = WorkerManager()
        cfg = {"provider": "openai", "openai": {"base_url": self.base_url, "api_key": "local-test-key", "model": "stream"}}
        def disconnected(_):
            raise WorkerError("cancelled", "client disconnected")
        try:
            with self.assertRaises(WorkerError):
                manager.run_translate("Hello", cfg, "zh", on_delta=disconnected, request_key="gone")
            self.assertIsNone(manager._worker)
            result = manager.run_translate("Hello", cfg, "zh")
            self.assertEqual(result["text"], "你好，世界！")
        finally:
            manager.dispose()


class TestTargetedCancellation(unittest.TestCase):
    def test_cancelling_queued_translation_does_not_kill_active_ocr(self):
        manager = WorkerManager()
        active = _FakeWorker(block=True)
        with patch.object(manager, "_spawn", return_value=active), patch.object(manager, "_schedule_idle"), ThreadPoolExecutor(max_workers=2) as pool:
            ocr = pool.submit(manager.run_ocr, b"image")
            self.assertTrue(active.reading.wait(2))
            translation = pool.submit(manager.run_translate, "Hello", {}, "zh", request_key="queued")
            manager.cancel_current("queued")
            with self.assertRaises(WorkerError) as error:
                translation.result(timeout=2)
            self.assertEqual(error.exception.code, "cancelled")
            self.assertFalse(active.killed.is_set())
            manager.cancel_current()
            with self.assertRaises(WorkerError):
                ocr.result(timeout=2)

    def test_targeted_active_cancel_keeps_cancelled_error_contract(self):
        manager = WorkerManager()
        active = _FakeWorker(block=True)
        with patch.object(manager, "_spawn", return_value=active), patch.object(manager, "_schedule_idle"), ThreadPoolExecutor(max_workers=1) as pool:
            request = pool.submit(manager.run_translate, "Hello", {}, "zh", request_key="active")
            self.assertTrue(active.reading.wait(2))
            manager.cancel_current("active")
            with self.assertRaises(WorkerError) as error:
                request.result(timeout=2)
            self.assertEqual(error.exception.code, "cancelled")


if __name__ == "__main__":
    unittest.main()
