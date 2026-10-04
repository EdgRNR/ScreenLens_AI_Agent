"""Cancellation and worker failure must retain their IPC error contracts."""
import io
import json
import os
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
from unittest.mock import Mock, patch

from screenlens.agent.workers import WorkerError, WorkerManager
from screenlens.ipc.protocol import encode_frame, read_json_frame


class _FakeWorker:
    def __init__(self, *, block=False, crash=False):
        self.dead = False
        self.block = block
        self.crash = crash
        self.reading = threading.Event()
        self.killed = threading.Event()
        self.proc = Mock()
        self.proc.poll.side_effect = lambda: -9 if self.killed.is_set() else None
        self.proc.kill.side_effect = self.killed.set

    def send_ctrl(self, ctrl):
        pass

    def send_binary(self, data):
        pass

    def read_json(self):
        self.reading.set()
        if self.block and not self.killed.wait(3):
            raise AssertionError("test worker was not cancelled")
        if self.block or self.crash:
            self.dead = True
            raise WorkerError("worker_crashed", "worker 进程已退出")
        return {"ok": True, "data": {"text": "next capture"}}


class TestWorkerCancellation(unittest.TestCase):
    def test_cancelled_eof_and_next_request_restarts_worker(self):
        manager = WorkerManager()
        active = _FakeWorker(block=True)
        replacement = _FakeWorker()
        with patch.object(manager, "_spawn", side_effect=[active, replacement]) as spawn, \
                patch.object(manager, "_schedule_idle"), ThreadPoolExecutor(max_workers=1) as pool:
            request = pool.submit(manager.run_ocr, b"image")
            self.assertTrue(active.reading.wait(2), "OCR entered the response wait")
            manager.cancel_current()
            with self.assertRaises(WorkerError) as raised:
                request.result(timeout=2)
            self.assertEqual(raised.exception.code, "cancelled")
            self.assertEqual(raised.exception.message, "任务已取消")
            self.assertEqual(manager.run_ocr(b"next image"), {"text": "next capture"})
            self.assertEqual(spawn.call_count, 2)

    def test_unrequested_worker_exit_remains_a_crash(self):
        manager = WorkerManager()
        with patch.object(manager, "_spawn", return_value=_FakeWorker(crash=True)), \
                patch.object(manager, "_schedule_idle"):
            with self.assertRaises(WorkerError) as raised:
                manager.run_ocr(b"image")
            self.assertEqual(raised.exception.code, "worker_crashed")
            self.assertEqual(raised.exception.message, "worker 进程已退出")


class _MemoryPipe:
    def __init__(self, op, *, disconnected=False):
        request = {"v": 1, "id": 7, "op": op, "data": {"text": "example"}}
        frames = encode_frame(json.dumps(request).encode("utf-8"))
        if op == "RecognizeImage":
            frames += encode_frame(b"image")
        self.input = io.BytesIO(frames)
        self.output = io.BytesIO()
        self.disconnected = disconnected

    def read_exact(self, length):
        data = self.input.read(length)
        return data if len(data) == length else None

    def write_all(self, data):
        if self.disconnected:
            raise BrokenPipeError("client closed")
        self.output.write(data)

    def response(self):
        self.output.seek(0)
        return read_json_frame(self.output.read)


@unittest.skipUnless(os.name == "nt", "agent IPC imports Windows pipe APIs")
class TestCancellationIpc(unittest.TestCase):
    def setUp(self):
        from screenlens.agent.app import HeadlessAgent

        # Bypass tray/config construction: no real agent, hotkeys, OCR, or user
        # settings are started or modified by these connection-handler tests.
        self.agent = HeadlessAgent.__new__(HeadlessAgent)
        self.agent._stop = threading.Event()
        self.agent.workers = Mock()
        self.agent.config = SimpleNamespace(translation={})

    def test_worker_errors_keep_code_and_message_without_internal_traceback(self):
        for op in ("RecognizeImage", "TranslateText"):
            for code in ("cancelled", "worker_crashed", "ocr_failed"):
                with self.subTest(op=op, code=code):
                    error = WorkerError(code, "任务已取消" if code == "cancelled" else "实际错误")
                    self.agent.workers.run_ocr.side_effect = error
                    self.agent.workers.run_translate.side_effect = error
                    pipe = _MemoryPipe(op)
                    with patch("screenlens.agent.app.logger") as log:
                        self.agent._handle_connection(pipe)
                    self.assertEqual(pipe.response(), {
                        "v": 1, "id": 7, "ok": False,
                        "error": {"code": code, "message": error.message},
                    })
                    log.exception.assert_not_called()

    def test_cancelled_reply_after_disconnect_is_only_debug(self):
        self.agent.workers.run_ocr.side_effect = WorkerError("cancelled", "任务已取消")
        with patch("screenlens.agent.app.logger") as log:
            self.agent._handle_connection(_MemoryPipe("RecognizeImage", disconnected=True))
        log.exception.assert_not_called()
        log.warning.assert_not_called()
        log.debug.assert_called_once()

    def test_late_success_after_disconnect_is_not_an_ipc_exception(self):
        self.agent.workers.run_ocr.return_value = {"text": "late result"}
        with patch("screenlens.agent.app.logger") as log:
            self.agent._handle_connection(_MemoryPipe("RecognizeImage", disconnected=True))
        log.exception.assert_not_called()
        log.warning.assert_called_once()

    def test_unexpected_handler_bug_still_logs_and_returns_internal(self):
        self.agent.workers.run_ocr.side_effect = RuntimeError("unexpected bug")
        pipe = _MemoryPipe("RecognizeImage")
        with patch("screenlens.agent.app.logger") as log:
            self.agent._handle_connection(pipe)
        log.exception.assert_called_once()
        self.assertEqual(pipe.response()["error"]["code"], "internal")


if __name__ == "__main__":
    unittest.main()
