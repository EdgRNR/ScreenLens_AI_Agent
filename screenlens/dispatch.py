# -*- coding: utf-8 -*-
"""主线程调度器：任何线程都可以安全地把任务投递到 Tk 主线程执行。

规则：Tk 控件与 root.after 等 Tk API 只允许在主线程调用；
后台线程（keyboard 钩子线程、pystray 托盘线程、OCR/翻译工作线程）
一律通过 MainThreadDispatcher.post() 转交主线程。
"""
import logging
import queue

logger = logging.getLogger(__name__)


class MainThreadDispatcher:
    def __init__(self, root, interval_ms: int = 20):
        self._root = root
        self._interval = interval_ms
        self._queue: queue.Queue = queue.Queue()
        self._running = False
        self._after_id = None

    @property
    def running(self) -> bool:
        return self._running

    def start(self):
        if self._running:
            return
        self._running = True
        self._schedule()

    def stop(self):
        """停止调度。必须在 root.destroy() 之前调用，避免向已销毁的
        Tk 对象继续安排回调。"""
        self._running = False
        if self._after_id is not None:
            try:
                self._root.after_cancel(self._after_id)
            except Exception:
                pass
            self._after_id = None
        # 丢弃未执行的任务，防止 destroy 后误触发
        while True:
            try:
                self._queue.get_nowait()
            except queue.Empty:
                break

    def post(self, fn, *args, **kwargs):
        """线程安全：把调用投递到主线程执行。任何线程可调用。"""
        if not self._running:
            return False
        self._queue.put((fn, args, kwargs))
        return True

    # ------------------------------------------------------------------

    def _schedule(self):
        if not self._running:
            return
        try:
            self._after_id = self._root.after(self._interval, self._drain)
        except Exception:
            # root 已销毁等情形
            self._running = False

    def _drain(self):
        executed = 0
        while True:
            try:
                fn, args, kwargs = self._queue.get_nowait()
            except queue.Empty:
                break
            try:
                fn(*args, **kwargs)
            except Exception:
                logger.exception("dispatched task failed: %r", fn)
            executed += 1
            if executed >= 100:  # 防止单次循环过长阻塞 UI
                break
        self._schedule()
