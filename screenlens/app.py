# -*- coding: utf-8 -*-
"""ScreenLens 主应用：托盘常驻 + 全局热键 + 截图 → OCR → 翻译。"""
import logging
import os
import subprocess
import sys
import tkinter as tk

from screenlens.capture import screen as screen_util
from screenlens.capture.overlay import CaptureOverlay
from screenlens.config import Config, config_path
from screenlens.hotkey import HotkeyManager
from screenlens.ocr.engine import OcrEngine
from screenlens.ui.result_window import ResultWindow

logger = logging.getLogger(__name__)


class ScreenLensApp:
    def __init__(self):
        self.config = Config()
        screen_util.set_dpi_awareness()

        self.root = tk.Tk()
        self.root.withdraw()  # 无主窗口，后台常驻
        self.root.title("ScreenLens")

        self.ocr_engine = OcrEngine()
        self.ocr_engine.warmup_async()

        self.result_window = ResultWindow(
            self.root, self.ocr_engine, self.config,
            on_recapture=self.request_capture)

        self.capturing = False
        self.overlay: CaptureOverlay | None = None
        self.tray_icon = None

        self.hotkeys = HotkeyManager(self._hotkey_fired)

    # ------------------------------------------------------------- startup

    def start(self) -> int:
        ok, msg = self.hotkeys.register(self.config.hotkey)
        if not ok:
            self._notify(f"快捷键注册失败：{msg}\n"
                         "请修改配置文件中的 hotkey 后重载。")

        from screenlens import tray as tray_mod
        tray_mod.run_tray(self)

        self.ocr_engine.warmup_async()
        self.root.mainloop()
        return 0

    # ------------------------------------------------------------ hotkeys

    @property
    def hotkey_display(self) -> str:
        return self.config.hotkey

    def _hotkey_fired(self):
        """热键触发（可能在 keyboard 库线程）→ 转到 tkinter 主线程。"""
        self.root.after(0, self.request_capture)

    def request_capture(self):
        if self.capturing:
            return
        self.capturing = True
        self.result_window.hide()
        self.overlay = CaptureOverlay(
            self.root,
            on_captured=self._on_captured,
            on_cancel=self._on_capture_cancel)
        self.overlay.start()

    def _on_capture_cancel(self, _reason):
        self.capturing = False
        self.overlay = None

    def _on_captured(self, crop, screen_bbox):
        self.capturing = False
        self.overlay = None
        self.result_window.show_ocr(crop, screen_bbox)

    # ---------------------------------------------------------- tray menu

    def open_config_file(self):
        path = config_path()
        if not os.path.isfile(path):
            self.config.save()
        try:
            os.startfile(path)  # type: ignore[attr-defined]
        except Exception:
            subprocess.Popen(["notepad.exe", path])

    def reload_config(self):
        self.config.load()
        self.result_window.reload_config()
        ok, msg = self.hotkeys.register(self.config.hotkey)
        self._notify(
            f"配置已重载。\n快捷键：{self.config.hotkey}"
            + ("" if ok else f"\n快捷键注册失败：{msg}"))

    def show_about(self):
        self._notify(
            "ScreenLens — 屏幕取词工具\n\n"
            f"快捷键：{self.config.hotkey}\n"
            "OCR：本地 RapidOCR（离线）\n"
            f"翻译：{self.config.translation.get('provider', 'none')}\n\n"
            "自由圈选屏幕内容 → 自动识别 → 复制 / 翻译")

    def quit(self):
        logger.info("quitting")
        try:
            self.hotkeys.shutdown()
        except Exception:
            pass
        if self.tray_icon is not None:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
        self.root.after(0, self.root.destroy)

    # ------------------------------------------------------------ helpers

    def _notify(self, message: str):
        """托盘气泡通知；不可用时退化为日志。"""
        logger.info("notify: %s", message)
        if self.tray_icon is not None:
            try:
                self.tray_icon.notify(message, "ScreenLens")
                return
            except Exception:
                pass
        from tkinter import messagebox
        self.root.after(0, lambda: messagebox.showinfo(
            "ScreenLens", message))


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        stream=sys.stderr)
    app = ScreenLensApp()
    try:
        return app.start()
    except KeyboardInterrupt:
        app.quit()
        return 0


if __name__ == "__main__":
    sys.exit(main())
