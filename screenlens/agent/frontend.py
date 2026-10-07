# -*- coding: utf-8 -*-
"""WinUI 前端进程定位与启动。

前端是按需进程：设置窗口或截图流程需要时才启动，窗口全部关闭后
进程自然退出（不隐藏驻留）。本模块负责：
- 在开发布局 / 打包布局中定位 ScreenLens.WinUI.exe；
- 以正确参数启动前端并看护其退出（回收 Popen 记录）；
- 提供存活查询，防止热键重复唤起。
"""
from __future__ import annotations

import logging
import os
import subprocess
import sys
import threading

logger = logging.getLogger(__name__)

# 开发布局的构建产物相对路径（相对仓库根）
_DEV_REL = os.path.join(
    "winui", "ScreenLens.WinUI", "bin", "x64", "{config}",
    "net8.0-windows10.0.19041.0", "win-x64", "ScreenLens.WinUI.exe")
_DEV_REL_ANYCPU = os.path.join(
    "winui", "ScreenLens.WinUI", "bin", "{config}",
    "net8.0-windows10.0.19041.0", "win-x64", "ScreenLens.WinUI.exe")


def _repo_root() -> str:
    # screenlens/agent/frontend.py -> screenlens/agent -> screenlens -> 仓库根
    return os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))


def find_frontend_exe() -> str | None:
    """按优先级定位 WinUI 前端 exe。"""
    candidates: list[str] = []
    env = os.environ.get("SCREENLENS_WINUI_EXE")
    if env:
        candidates.append(env)
    # 打包布局：代理 exe 同目录
    exe_dir = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False)
                                             else sys.argv[0] or "."))
    candidates.append(os.path.join(exe_dir, "ScreenLens.WinUI.exe"))
    # 开发布局：仓库构建产物（Debug 优先，回退 Release）
    root = _repo_root()
    for cfg in ("Debug", "Release"):
        candidates.append(os.path.join(root, _DEV_REL.format(config=cfg)))
        candidates.append(os.path.join(
            root, _DEV_REL_ANYCPU.format(config=cfg)))
    for path in candidates:
        if os.path.isfile(path):
            return path
    return None


class FrontendManager:
    """启动并看护按需前端进程。"""

    def __init__(self):
        self._lock = threading.Lock()
        self._procs: dict[int, dict] = {}  # pid -> {"proc": Popen, "mode": str}
        self._exe: str | None = None

    @property
    def exe_path(self) -> str | None:
        # Agent 常在 WinUI 首次构建前启动。不能把当时的“未找到”永久缓存，
        # 否则之后即使 VS 已生成前端，热键也会一直启动失败。
        if self._exe is None or not os.path.isfile(self._exe):
            self._exe = find_frontend_exe()
            if not self._exe:
                logger.warning("未找到 WinUI 前端 exe（SCREENLENS_WINUI_EXE/"
                               "打包布局/开发布局均未命中）")
        return self._exe

    def launch(self, *args: str) -> int | None:
        """向 WinUI 投递模式请求；WinUI 单实例负责聚焦/路由到已有窗口。

        调用方可传内部模式名（settings/capture），本方法负责映射为
        WinUI 命令行开关。每次都允许启动短暂的转发进程，以便已有实例
        收到激活请求，而不是静默忽略重复热键。
        """
        mode = (args[0] if args else "settings").lstrip("-/").lower()
        mode_arg = {"capture": "--capture", "cancel-capture": "--cancel-capture"}.get(mode, "--settings")
        extra_args = list(args[1:]) if args else []
        exe = self.exe_path
        if not exe:
            return None

        with self._lock:
            self._reap_locked()
            # WinUI's single-instance coordinator forwards the action to an
            # existing window. Do not drop a different capture action here.
            try:
                # Popen 也放在锁内，避免多个键盘回调同时通过上面的存活检查，
                # 然后各自启动一个 WinUI 子进程。
                proc = subprocess.Popen(
                    [exe, mode_arg, *extra_args], cwd=os.path.dirname(exe),
                    close_fds=True)
            except OSError as e:
                logger.error("前端启动失败: %s", e)
                return None
            self._procs[proc.pid] = {"proc": proc, "mode": mode}

        threading.Thread(target=self._waiter, args=(proc,),
                         daemon=True, name=f"frontend-{proc.pid}").start()
        logger.info("前端激活请求进程已创建 mode=%s pid=%s exe=%s",
                    mode, proc.pid, exe)
        return proc.pid

    def _waiter(self, proc: subprocess.Popen) -> None:
        try:
            proc.wait()
        finally:
            with self._lock:
                self._procs.pop(proc.pid, None)
            logger.info("前端已退出 pid=%s code=%s", proc.pid, proc.returncode)

    def _reap_locked(self) -> None:
        dead = [pid for pid, info in self._procs.items()
                if info["proc"].poll() is not None]
        for pid in dead:
            self._procs.pop(pid, None)

    def _alive_of_mode_locked(self, mode: str) -> bool:
        return any(info["mode"] == mode for info in self._procs.values())

    def frontend_pids(self) -> list[int]:
        with self._lock:
            self._reap_locked()
            return list(self._procs.keys())

    def shutdown(self) -> None:
        with self._lock:
            procs = [info["proc"] for info in self._procs.values()]
        for p in procs:
            try:
                p.terminate()
            except Exception:
                pass
