# -*- coding: utf-8 -*-
"""OCR / 翻译 worker 子进程管理（短生命周期复用）。

策略：
- 收到 RecognizeImage / TranslateText 时按需 spawn worker；
- 任务完成后保留 15 秒供后续任务复用（连续识别-翻译快路径），
  15 秒空闲后通知退出，超时强杀——重内存随进程退出彻底回收；
- 同一时刻只跑一个任务（内部锁串行），并发请求排队；
- worker 崩溃（EOF）时对上层返回 worker_crashed，下次任务自动重启；
- 取消：cancel_current() 终止 worker，进行中的调用收到 cancelled。
"""
from __future__ import annotations

import json
import logging
import os
import subprocess
import sys
import threading
from collections import OrderedDict

from screenlens.ipc.protocol import encode_frame, read_frame

logger = logging.getLogger(__name__)

_IDLE_TTL = 15.0       # 空闲保留秒数
_EXIT_GRACE = 3.0      # 优雅退出宽限
_TASK_TIMEOUT = 90.0    # 单任务看门狗（防 worker 挂死）


class WorkerError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


class _WorkerProc:
    """一个 worker 子进程的 stdin/stdout 帧交互。"""

    def __init__(self, proc: subprocess.Popen):
        self.proc = proc
        self.dead = False

    def send_ctrl(self, obj: dict) -> None:
        try:
            self.proc.stdin.write(encode_frame(
                json.dumps(obj, ensure_ascii=False).encode("utf-8")))
            self.proc.stdin.flush()
        except (OSError, ValueError):
            self.dead = True
            raise WorkerError("worker_crashed", "worker 管道已断开")

    def send_binary(self, data: bytes) -> None:
        try:
            self.proc.stdin.write(encode_frame(data))
            self.proc.stdin.flush()
        except (OSError, ValueError):
            self.dead = True
            raise WorkerError("worker_crashed", "worker 管道已断开")

    def read_json(self):
        """读响应控制帧；EOF/坏帧抛 WorkerError。"""
        def read_exact(n: int):
            data = self.proc.stdout.read(n)
            if data is None or len(data) < n:
                return None
            return data
        try:
            frame = read_frame(read_exact)
        except Exception as e:
            self.dead = True
            raise WorkerError("worker_crashed", f"worker 响应帧错误: {e}")
        if frame is None:
            self.dead = True
            raise WorkerError("worker_crashed", "worker 进程已退出")
        try:
            obj = json.loads(frame.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            self.dead = True
            raise WorkerError("worker_crashed", "worker 响应不是合法 JSON")
        return obj

    def dispose(self) -> None:
        try:
            self._stop_process()
        finally:
            # Explicitly close dead pipes: a buffered stdin may otherwise
            # flush against a killed worker during garbage collection.
            for stream in (self.proc.stdin, self.proc.stdout, self.proc.stderr):
                if stream is not None:
                    try:
                        stream.close()
                    except (OSError, ValueError):
                        pass
            self.dead = True

    def _stop_process(self) -> None:
        if self.dead:
            try:
                self.proc.kill()
                self.proc.wait(timeout=_EXIT_GRACE)
            except Exception:
                pass
            return
        try:
            self.proc.stdin.write(encode_frame(
                json.dumps({"op": "exit", "id": 0}).encode("utf-8")))
            self.proc.stdin.flush()
            try:
                self.proc.wait(timeout=_EXIT_GRACE)
                return
            except subprocess.TimeoutExpired:
                pass
        except Exception:
            pass
        try:
            self.proc.kill()
            self.proc.wait(timeout=_EXIT_GRACE)
        except Exception:
            pass


class WorkerManager:
    """串行任务队列 + 按 TTL 回收的单 worker 管理。"""

    def __init__(self):
        self._lock = threading.Lock()       # 任务串行
        self._state_lock = threading.Lock()
        self._worker: _WorkerProc | None = None
        self._idle_timer: threading.Timer | None = None
        self._current_cancelled = threading.Event()
        self._active_request_key = None
        self._cancelled_keys = OrderedDict()

    # ------------------------------------------------------------ 公共 API

    def run_ocr(self, png: bytes) -> dict:
        """识别一张 PNG，返回 worker 响应 data。"""
        return self._execute({"op": "ocr", "id": 1}, binary=png)

    def run_translate(self, text: str, translation_cfg: dict,
                      target_lang: str, *, on_delta=None, request_key=None) -> dict:
        return self._execute({
            "op": "translate", "id": 2,
            "data": {"text": text, "config": translation_cfg,
                     "target_language": target_lang, "stream": on_delta is not None}},
            on_delta=on_delta, request_key=request_key)

    def translation_setup(self, op: str, data: dict, *, request_key=None) -> dict:
        return self._execute({"op": op, "id": 3, "data": data}, request_key=request_key)

    def cancel_current(self, request_key=None) -> None:
        """取消进行中的任务（终止 worker；由下一次任务自动重启）。"""
        with self._state_lock:
            if request_key is not None:
                self._cancelled_keys[request_key] = None
                while len(self._cancelled_keys) > 128:
                    self._cancelled_keys.popitem(last=False)
                if request_key != self._active_request_key:
                    return
            self._current_cancelled.set()
            w = self._worker
        if w is not None:
            try:
                w.proc.kill()
            except Exception:
                pass

    def busy(self) -> bool:
        return self._lock.locked()

    def dispose(self) -> None:
        with self._state_lock:
            w, self._worker = self._worker, None
            timer, self._idle_timer = self._idle_timer, None
        if timer is not None:
            timer.cancel()
        if w is not None:
            w.dispose()

    # ------------------------------------------------------------ 内部实现

    def _execute(self, ctrl: dict, binary: bytes | None = None, *, on_delta=None,
                 request_key=None) -> dict:
        def check_cancelled():
            with self._state_lock:
                cancelled = request_key is not None and request_key in self._cancelled_keys
            if cancelled:
                raise WorkerError("cancelled", "任务已取消")

        while not self._lock.acquire(timeout=0.1):
            check_cancelled()
        w = None
        complete = False
        try:
            self._current_cancelled.clear()
            with self._state_lock:
                self._active_request_key = request_key
            check_cancelled()
            self._cancel_idle_timer()
            try:
                w = self._ensure_worker()
                check_cancelled()
                watchdog = threading.Timer(
                    _TASK_TIMEOUT, self._kill_current)
                watchdog.daemon = True
                watchdog.start()
                try:
                    w.send_ctrl(ctrl)
                    if binary is not None:
                        w.send_binary(binary)
                    resp = w.read_json()
                    while resp.get("event") == "delta":
                        if on_delta is None:
                            raise WorkerError("internal", "非流式任务收到增量响应")
                        check_cancelled()
                        on_delta((resp.get("data") or {}).get("text") or "")
                        resp = w.read_json()
                    complete = True
                finally:
                    watchdog.cancel()
                if resp.get("ok"):
                    return resp.get("data") or {}
                err = resp.get("error") or {}
                raise WorkerError(err.get("code", "internal"),
                                  err.get("message", "worker 返回错误"))
            except WorkerError as e:
                if self._current_cancelled.is_set():
                    raise WorkerError("cancelled", "任务已取消") from e
                raise
            finally:
                self._schedule_idle()
        finally:
            # A disconnected stream must not leave unread frames in a reused worker.
            if w is not None and not complete:
                with self._state_lock:
                    if self._worker is w:
                        self._worker = None
                w.dead = True
                w.dispose()
            with self._state_lock:
                self._active_request_key = None
                self._cancelled_keys.pop(request_key, None)
            self._lock.release()

    def _ensure_worker(self) -> _WorkerProc:
        with self._state_lock:
            w = self._worker
            if w is not None and not w.dead and w.proc.poll() is None:
                return w
            self._worker = None
            if w is not None:
                w.dispose()
            w = self._spawn()
            self._worker = w
            return w

    @staticmethod
    def _python_executable() -> str:
        """worker 必须用【带依赖的解释器】启动。

        注意：不能用 sys._base_executable —— 那是 venv 之外的基础解释器，
        看不到 venv 的 site-packages，会直接 ModuleNotFoundError(PIL)。
        venv 的 sys.executable 在 Windows 上是启动器 shim，会额外产生一个
        ~1 MiB 的转发进程，属可接受的短暂开销。
        若代理被 PyInstaller 打包（sys.frozen），可用环境变量
        SCREENLENS_WORKER_EXE 显式指定带依赖的解释器。
        """
        if getattr(sys, "frozen", False):
            return os.environ.get("SCREENLENS_WORKER_EXE", sys.executable)
        return sys.executable

    def _spawn(self) -> _WorkerProc:
        exe = self._python_executable()
        repo_root = os.path.dirname(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))))
        try:
            proc = subprocess.Popen(
                [exe, "-m", "screenlens.worker"],
                cwd=repo_root,               # 仓库根，保证 -m 找到包
                stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, close_fds=True)
        except OSError as e:
            raise WorkerError("worker_crashed", f"worker 启动失败: {e}") from e
        logger.info("worker 已启动 pid=%s", proc.pid)
        return _WorkerProc(proc)

    def _kill_current(self) -> None:
        logger.warning("worker 任务超时（%.0fs），强制终止", _TASK_TIMEOUT)
        with self._state_lock:
            w = self._worker
        if w is not None:
            try:
                w.proc.kill()
            except Exception:
                pass

    def _schedule_idle(self) -> None:
        with self._state_lock:
            self._cancel_idle_timer_locked()
            self._idle_timer = threading.Timer(
                _IDLE_TTL, self._idle_exit)
            self._idle_timer.daemon = True
            self._idle_timer.start()

    def _cancel_idle_timer(self) -> None:
        with self._state_lock:
            self._cancel_idle_timer_locked()

    def _cancel_idle_timer_locked(self) -> None:
        if self._idle_timer is not None:
            self._idle_timer.cancel()
            self._idle_timer = None

    def _idle_exit(self) -> None:
        with self._state_lock:
            w, self._worker = self._worker, None
            self._cancel_idle_timer_locked()
        if w is not None:
            logger.info("worker 空闲 %.0fs，退出回收", _IDLE_TTL)
            w.dispose()
