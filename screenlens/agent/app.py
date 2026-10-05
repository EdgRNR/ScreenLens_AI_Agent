# -*- coding: utf-8 -*-
"""headless 后台代理：托盘 + 全局热键 + 命名管道 IPC，无任何 UI 框架。

进程边界（见阶段计划书第 3 节）：
- 本进程只做轻量常驻：托盘 / 热键 / 配置 / IPC / 前端唤起；
- 不 import tkinter，不加载 OCR 模型（不预热）；
- OCR / 翻译由 WorkerManager 按需 spawn 的短生命周期 worker 完成；
- WinUI 前端由 FrontendManager 按需启动，窗口关闭即进程退出。
"""
from __future__ import annotations

import json
import logging
import logging.handlers
import os
import sys
import threading
import time
import ctypes

from screenlens.config import Config, validate_config
from screenlens.hotkey import HotkeyManager
from screenlens.ipc.pipe_server import PipeServer
from screenlens.ipc.protocol import (
    PIPE_NAME,
    PROTOCOL_VERSION,
    encode_frame,
    read_frame,
    read_json_frame,
)
from screenlens.agent.frontend import FrontendManager
from screenlens.agent.workers import WorkerError, WorkerManager

logger = logging.getLogger("screenlens.agent")
_AGENT_MUTEX_NAME = r"Local\ScreenLens.Agent"


def _acquire_agent_mutex():
    """跨进程原子锁，避免启动竞态产生多个托盘代理。"""
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    create_mutex = kernel.CreateMutexW
    create_mutex.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p]
    create_mutex.restype = ctypes.c_void_p
    close_handle = kernel.CloseHandle
    close_handle.argtypes = [ctypes.c_void_p]
    close_handle.restype = ctypes.c_int
    ctypes.set_last_error(0)
    # Existence lock only: do not acquire ownership because shutdown can be
    # triggered from the tray callback thread while the main thread serves IPC.
    handle = create_mutex(None, 0, _AGENT_MUTEX_NAME)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    if ctypes.get_last_error() == 183:  # ERROR_ALREADY_EXISTS
        close_handle(handle)
        return None
    return kernel, handle


class IpcError(Exception):
    """带错误码的 IPC 处理失败。"""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


def _agent_already_running() -> bool:
    """尝试作为客户端连一次管道：连通说明已有代理在跑。"""
    try:
        with open(PIPE_NAME, "rb", buffering=0) as f:  # noqa: F841
            return True
    except OSError:
        return False


class HeadlessAgent:
    def __init__(self):
        self.config = Config()
        self.hotkeys = HotkeyManager(self._on_hotkey)
        self.frontends = FrontendManager()
        self.workers = WorkerManager()
        self._started_at = time.time()
        self._stop = threading.Event()
        self._hotkey_mutex = threading.Lock()
        self._last_hotkey_launch = 0.0
        self.tray_icon = None
        self._server: PipeServer | None = None
        self._agent_mutex = None

    # -------------------------------------------------------------- 启停

    def start(self) -> int:
        self._agent_mutex = _acquire_agent_mutex()
        if self._agent_mutex is None:
            logger.info("已有 ScreenLens 代理持有单实例锁，本次启动退出")
            return 0
        if _agent_already_running():
            logger.error("已有 ScreenLens 代理在运行，本次启动退出")
            self._release_agent_mutex()
            return 0

        startup_notices = []
        if self.config.load_error:
            startup_notices.append(
                f"配置文件存在以下问题，已回退默认值：\n"
                f"{self.config.load_error}\n\n"
                "可打开「设置」重新保存配置。")
        ok, msg = self.hotkeys.register(self.config.hotkey)
        if not ok:
            startup_notices.append(
                f"快捷键注册失败：{msg}\n请在「设置」中更换快捷键。")

        self._start_tray()
        # 托盘初始化前 _notify 只能写日志，通知会悄悄丢失；启动完托盘后再显示。
        for notice in startup_notices:
            self._notify(notice)

        self._server = PipeServer(PIPE_NAME, self._handle_connection)
        try:
            self._server.serve_forever()
        finally:
            self.shutdown()
        return 0

    def shutdown(self) -> None:
        if self._stop.is_set():
            return
        self._stop.set()
        logger.info("代理退出")
        try:
            self.hotkeys.shutdown()
        except Exception:
            pass
        if self.tray_icon is not None:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
        self.workers.dispose()
        self.frontends.shutdown()
        self._release_agent_mutex()

    def _release_agent_mutex(self) -> None:
        if self._agent_mutex is None:
            return
        kernel, handle = self._agent_mutex
        self._agent_mutex = None
        try:
            kernel.CloseHandle(handle)
        except Exception:
            pass

    # ---------------------------------------------------------- 托盘/热键

    def _start_tray(self) -> None:
        import pystray
        from screenlens.tray import _make_icon_image

        def _capture(icon, item):
            self._launch_capture()

        def _settings(icon, item):
            self.frontends.launch("settings")

        def _quit(icon, item):
            self.shutdown()
            if self._server is not None:
                self._server.stop()

        menu = pystray.Menu(
            pystray.MenuItem("截图（全局快捷键）", _capture, default=True),
            pystray.MenuItem("设置…", _settings),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(
                lambda item: f"快捷键：{self.config.hotkey}",
                None, enabled=False),
            pystray.MenuItem("退出", _quit),
        )
        try:
            self.tray_icon = pystray.Icon(
                "ScreenLens", _make_icon_image(), "ScreenLens", menu)
            self.tray_icon.run_detached()
            logger.info("托盘已启动（headless）")
        except Exception:
            logger.exception("托盘启动失败（代理继续运行）")

    def _on_hotkey(self) -> None:
        """keyboard 钩子线程调用；直接启动前端，无 Tk 依赖。"""
        logger.info("收到全局截图快捷键，准备启动 WinUI 截图流程")
        self._launch_capture()

    def _launch_capture(self) -> None:
        with self._hotkey_mutex:
            if self._stop.is_set():
                return
            now = time.monotonic()
            if now - self._last_hotkey_launch < 0.8:
                logger.info("忽略 800ms 内重复触发的截图快捷键")
                return
            self._last_hotkey_launch = now
            pid = self.frontends.launch("capture")
            if pid is None:
                self._last_hotkey_launch = 0.0
                exe = self.frontends.exe_path
                if exe is None:
                    message = "截图未启动：找不到 WinUI 前端程序。请先在 Visual Studio 构建并运行一次。"
                else:
                    message = "截图未启动：WinUI 前端进程启动失败。请查看 %LOCALAPPDATA%\\ScreenLens\\logs\\agent.log。"
                logger.error("%s (exe=%s)", message, exe)
                self._notify(message)

    def _notify(self, message: str):
        logger.info("notify: %s", message.splitlines()[0])
        if self.tray_icon is not None:
            try:
                self.tray_icon.notify(message, "ScreenLens")
                return
            except Exception:
                pass
        # 无托盘时仅记日志，绝不弹任何窗口

    # ------------------------------------------------------------- IPC

    def _handle_connection(self, pipe) -> None:
        """每个连接一个线程；顺序处理请求-响应。"""
        while not self._stop.is_set():
            try:
                req = read_json_frame(pipe.read_exact)
            except Exception as e:
                logger.warning("连接异常关闭: %s", e)
                return
            if req is None:
                return
            try:
                self._dispatch(pipe, req)
            except IpcError as e:
                self._safe_respond(pipe, req.get("id", -1),
                                   error_code=e.code, message=e.message)
            except Exception as e:
                logger.exception("IPC 处理异常 op=%s", req.get("op"))
                self._safe_respond(pipe, req.get("id", -1),
                                   error_code="internal", message=str(e))

    def _respond(self, pipe, req_id: int, *, data: dict | None = None,
                 error_code: str | None = None,
                 message: str | None = None, event: str | None = None) -> None:
        resp = {"v": PROTOCOL_VERSION, "id": req_id,
                "ok": error_code is None}
        if error_code is None:
            resp["data"] = data or {}
            if event:
                resp["event"] = event
        else:
            resp["error"] = {"code": error_code,
                             "message": message or error_code}
        pipe.write_all(encode_frame(
            json.dumps(resp, ensure_ascii=False).encode("utf-8")))

    def _safe_respond(self, pipe, req_id: int, *, data: dict | None = None,
                      error_code: str | None = None,
                      message: str | None = None) -> None:
        """回错误响应；客户端已断开时只记日志，不打断连接线程。"""
        try:
            self._respond(pipe, req_id, data=data,
                          error_code=error_code, message=message)
        except Exception as e:
            # Closing the capture window can disconnect before the cancelled
            # reply is written. That is an expected end to a cancelled request.
            log = logger.debug if error_code == "cancelled" else logger.warning
            log("响应写入失败（客户端可能已断开）: %s", e)

    def _dispatch(self, pipe, req: dict) -> None:
        if req.get("v") != PROTOCOL_VERSION:
            raise IpcError("unsupported_version",
                           f"协议版本不匹配（服务端 v{PROTOCOL_VERSION}）")
        op = req.get("op")
        data = req.get("data") or {}
        req_id = req.get("id", -1)
        request_token = data.get("request_token")
        if request_token is not None and (not isinstance(request_token, str)
                                          or not request_token or len(request_token) > 128):
            raise IpcError("bad_request", "任务标识格式不正确")

        if op == "Ping":
            self._respond(pipe, req_id, data={"pong": True})

        elif op == "GetStatus":
            self._respond(pipe, req_id, data={
                "pid": os.getpid(),
                "uptime_s": round(time.time() - self._started_at, 1),
                "hotkey": self.config.hotkey,
                "provider": self.config.translation.get("provider"),
                "worker_busy": self.workers.busy(),
                "frontend_pids": self.frontends.frontend_pids(),
            })

        elif op == "GetSettings":
            self._respond(pipe, req_id, data={
                "config": self.config.as_dict(),
                "path": self.config.path,
            })

        elif op == "SaveSettings":
            self._op_save_settings(pipe, req_id, data)

        elif op == "RegisterHotkey":
            new_hk = (data.get("hotkey") or "").strip()
            if not new_hk:
                raise IpcError("bad_request", "快捷键不能为空")
            ok, msg = self.hotkeys.try_register(new_hk)
            if not ok:
                raise IpcError("hotkey_conflict",
                               f"{msg}（已保留 {self.config.hotkey}）")
            old_hk = self.config.hotkey
            self.config.hotkey = new_hk
            try:
                self.config.save()
            except Exception as e:
                self.config.hotkey = old_hk
                self.hotkeys.try_register(old_hk)
                raise IpcError("internal", f"配置写入失败：{e}") from e
            self._respond(pipe, req_id, data={"hotkey": new_hk})

        elif op == "RecognizeImage":
            self._op_recognize(pipe, req_id, data)

        elif op == "TranslateText":
            text = data.get("text") or ""
            cfg = self.config.translation
            target = data.get("target_language") or \
                cfg.get("target_language", "zh")
            try:
                if data.get("stream"):
                    def delta(chunk):
                        try:
                            self._respond(pipe, req_id, data={"text": chunk}, event="delta")
                        except (OSError, ValueError) as e:
                            raise WorkerError("cancelled", "任务已取消") from e
                    result = self.workers.run_translate(text, cfg, target, on_delta=delta,
                                                       request_key=data.get("request_token"))
                else:
                    result = self.workers.run_translate(text, cfg, target)
            except WorkerError as e:
                raise IpcError(e.code, e.message) from e
            self._safe_respond(pipe, req_id, data=result)

        elif op in ("ListTranslationModels", "TestTranslationConnection"):
            oa = data.get("openai")
            if not isinstance(oa, dict) or not all(isinstance(oa.get(k, ""), str)
                                                 for k in ("base_url", "api_key", "model")):
                raise IpcError("bad_request", "API 参数格式不正确")
            try:
                result = self.workers.translation_setup(
                    "models" if op == "ListTranslationModels" else "test_translation", data,
                    request_key=data.get("request_token"))
            except WorkerError as e:
                raise IpcError(e.code, e.message) from e
            self._safe_respond(pipe, req_id, data=result)

        elif op == "CancelRequest":
            if data.get("request_token"):
                self.workers.cancel_current(data["request_token"])
            else:
                self.workers.cancel_current()
            self._respond(pipe, req_id, data={})

        elif op == "Shutdown":
            self._respond(pipe, req_id, data={})
            self.shutdown()
            if self._server is not None:
                self._server.stop()

        else:
            raise IpcError("unknown_op", f"未知操作 {op}")

    # -------------------------------------------------------- IPC 处理器

    def _op_save_settings(self, pipe, req_id: int, data: dict) -> None:
        submitted = data.get("config")
        if not isinstance(submitted, dict):
            raise IpcError("bad_request", "缺少 config 字段")
        errors = validate_config(submitted)
        if errors:
            raise IpcError("config_invalid", "；".join(errors))

        new_hk = submitted.get("hotkey", self.config.hotkey)
        if new_hk != self.config.hotkey:
            ok, msg = self.hotkeys.try_register(new_hk)
            if not ok:
                raise IpcError("hotkey_conflict",
                               f"{msg}（已保留 {self.config.hotkey}）")
        old_hk = self.config.hotkey
        try:
            self.config.apply_dict(submitted)
        except Exception as e:
            if new_hk != old_hk:
                self.hotkeys.try_register(old_hk)
            raise IpcError("internal", f"配置写入失败：{e}") from e
        logger.info("设置已通过 IPC 保存（provider=%s hotkey=%s）",
                    submitted.get("translation", {}).get("provider"),
                    self.config.hotkey)
        self._respond(pipe, req_id, data={})

    def _op_recognize(self, pipe, req_id: int, data: dict) -> None:
        png = read_frame(pipe.read_exact)
        if png is None:
            logger.warning("RecognizeImage: 对端关闭，未收到图像帧")
            raise IpcError("bad_request", "缺少图像帧")
        if not png:
            logger.warning("RecognizeImage: 图像帧为空（0 字节）")
            raise IpcError("bad_request", "图像帧为空")
        logger.info("RecognizeImage: 收到 PNG %d 字节", len(png))
        try:
            result = self.workers.run_ocr(png)
        except WorkerError as e:
            raise IpcError(e.code, e.message) from e
        self._safe_respond(pipe, req_id, data=result)


def setup_logging() -> None:
    log_dir = os.path.join(
        os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"),
        "ScreenLens", "logs")
    handlers: list[logging.Handler] = [logging.StreamHandler(sys.stderr)]
    try:
        os.makedirs(log_dir, exist_ok=True)
        handlers.append(logging.handlers.RotatingFileHandler(
            os.path.join(log_dir, "agent.log"),
            maxBytes=1024 * 1024, backupCount=2, encoding="utf-8"))
    except OSError:
        pass
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        handlers=handlers)


def main() -> int:
    setup_logging()
    logger.info("headless 代理启动 pid=%s", os.getpid())
    agent = HeadlessAgent()
    try:
        return agent.start()
    except KeyboardInterrupt:
        agent.shutdown()
        return 0
