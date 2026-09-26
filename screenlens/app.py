# -*- coding: utf-8 -*-
"""ScreenLens 主应用：托盘常驻 + 全局热键 + 截图 → OCR → 翻译。

线程模型：
- Tk 主线程唯一负责所有 UI 与 Tk API；
- keyboard 热键线程、pystray 托盘线程只通过
  MainThreadDispatcher.post() 把任务投递到主线程；
- OCR / 翻译工作线程通过 ResultWindow 内部队列回传结果。
"""
import logging
import os
import subprocess
import sys
import tkinter as tk

from screenlens.capture import screen as screen_util
from screenlens.capture.overlay import CaptureOverlay
from screenlens.config import Config, config_path
from screenlens.dispatch import MainThreadDispatcher
from screenlens.hotkey import HotkeyManager
from screenlens.ocr.engine import OcrEngine

from screenlens.ui.result_window import ResultWindow
from screenlens.ui.settings_window import SettingsWindow

logger = logging.getLogger(__name__)


class ScreenLensApp:
    def __init__(self):
        self.config = Config()
        screen_util.set_dpi_awareness()

        self.root = tk.Tk()
        self.root.withdraw()  # 无主窗口，后台常驻
        self.root.title("ScreenLens")

        self.dispatcher = MainThreadDispatcher(self.root)
        self.dispatcher.start()

        self.ocr_engine = OcrEngine()
        self.ocr_engine.warmup_async()

        self.result_window = ResultWindow(
            self.root, self.ocr_engine, self.config,
            on_recapture=self.request_capture)

        self.settings_window = SettingsWindow(
            self.root, self.config, self._apply_settings_impl)

        self.capturing = False
        self.overlay: CaptureOverlay | None = None
        self.tray_icon = None

        self.hotkeys = HotkeyManager(self._on_hotkey_fired)

    # ------------------------------------------------------------- startup

    def start(self) -> int:
        if self.config.load_error:
            logger.warning("config load issue: %s", self.config.load_error)
            self._notify(f"配置文件存在以下问题，已回退默认值：\n"
                         f"{self.config.load_error}\n\n"
                         "可在托盘菜单打开「设置」重新保存配置。")
        ok, msg = self.hotkeys.register(self.config.hotkey)
        if not ok:
            self._notify(f"快捷键注册失败：{msg}\n"
                         "请在托盘「设置」中更换快捷键。")

        from screenlens import tray as tray_mod
        tray_mod.run_tray(self)

        self.root.mainloop()
        return 0

    # ------------------------------------------------------- thread safety

    def post(self, fn, *args, **kwargs):
        """把任务安全投递到 Tk 主线程（任意线程可调用）。"""
        return self.dispatcher.post(fn, *args, **kwargs)

    # --------------------------------------------------------------- hotkey

    @property
    def hotkey_display(self) -> str:
        return self.config.hotkey

    def _on_hotkey_fired(self):
        """keyboard 钩子线程调用：仅投递，不做任何 Tk 操作。"""
        self.dispatcher.post(self.request_capture)

    def request_capture(self):
        """开始截图（必须在主线程调用）。"""
        if self.capturing or self.settings_window.is_shown():
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

    # ------------------------------------------------------------ settings

    def open_settings(self):
        """打开设置窗口（主线程）。"""
        try:
            self.settings_window.show()
        except Exception:
            logger.exception("open settings failed")
            self._notify("设置窗口打开失败，请查看日志。")

    def _apply_settings_impl(self, hotkey: str, translation: dict) -> list:
        """应用设置（主线程）。返回错误列表；热键失败自动回滚。

        供 SettingsWindow 的 apply_callback 调用。
        """
        errors = []
        old_hotkey = self.config.hotkey

        # 1) 先尝试注册新热键（失败不影响当前注册）
        if hotkey != old_hotkey:
            ok, msg = self.hotkeys.try_register(hotkey)
            if ok:
                self.config.hotkey = hotkey
            else:
                errors.append(msg + "（已保留原快捷键 "
                              f"{old_hotkey}）")
                self.config.hotkey = old_hotkey

        # 2) 应用翻译配置
        self.config.set_translation(translation)
        self.result_window.reload_config()

        # 3) 持久化（原子写入）
        try:
            self.config.save()
        except Exception as e:
            errors.append(f"配置写入失败：{e}")
            logger.exception("config save failed")

        if errors:
            logger.warning("settings applied with errors: %s",
                           [e.split("：")[0] for e in errors])
        else:
            logger.info("settings applied: provider=%s hotkey=%s",
                       translation.get("provider"), self.config.hotkey)
        return errors

    # ---------------------------------------------------------- tray menu

    def open_config_file(self):
        self.post(self._open_config_file_impl)

    def _open_config_file_impl(self):
        path = config_path()
        if not os.path.isfile(path):
            self.config.save()
        try:
            os.startfile(path)  # type: ignore[attr-defined]
        except Exception:
            subprocess.Popen(["notepad.exe", path])

    def reload_config(self):
        self.post(self._reload_config_impl)

    def _reload_config_impl(self):
        self.config.load()
        self.result_window.reload_config()
        note = ""
        if self.config.load_error:
            note = f"\n注意：{self.config.load_error}"
        ok, msg = self.hotkeys.register(self.config.hotkey)
        self._notify(
            f"配置已重载。\n快捷键：{self.config.hotkey}"
            + ("" if ok else f"\n快捷键注册失败：{msg}") + note)

    def show_about(self):
        provider = self.config.translation.get("provider", "none")
        provider_name = {"google_free": "Google 免费翻译",
                         "openai": "OpenAI 兼容接口",
                         "none": "未启用（离线）"}.get(provider, provider)
        self._notify(
            "ScreenLens — 屏幕取词工具\n\n"
            f"快捷键：{self.config.hotkey}\n"
            "OCR：本地 RapidOCR（完全离线，不上传截图）\n"
            f"翻译：{provider_name}\n\n"
            "自由圈选屏幕内容 → 自动识别 → 复制 / 翻译\n"
            "设置入口：托盘右键菜单 → 设置")

    def quit(self):
        logger.info("quitting")
        # 顺序很重要：先停调度器和轮询，再销毁 Tk，
        # 防止 destroy 之后仍有回调被安排或执行
        try:
            self.hotkeys.shutdown()
        except Exception:
            pass
        if self.tray_icon is not None:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
        self.dispatcher.stop()
        try:
            self.result_window.close()
        except Exception:
            pass
        self.root.after(0, self._final_destroy)

    def _final_destroy(self):
        try:
            self.root.destroy()
        except tk.TclError:
            pass
        # 在主线程回收残余 Tk 对象，避免解释器退出时由其他线程触发
        import gc

        gc.collect()

    # ------------------------------------------------------------ helpers

    def _notify(self, message: str):
        """托盘气泡通知（主线程调用）；不可用时退化为弹窗。"""
        logger.info("notify: %s", message.splitlines()[0])
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
