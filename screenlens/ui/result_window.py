# -*- coding: utf-8 -*-
"""OCR 结果浮窗：轻量、无边框、置顶，出现在选区旁边。

功能：识别文字（可编辑）、一键复制、翻译（可选目标语言）、重新截图、关闭。
OCR 与翻译都在后台线程执行，不阻塞 UI。
"""
import queue
import threading
import tkinter as tk
import tkinter.ttk as ttk

from screenlens.capture import screen as screen_util
from screenlens.capture.region import upscale_for_ocr
from screenlens.ocr.engine import OcrEngine, OcrError
from screenlens.translate.provider import (
    LANG_NAMES,
    TranslationError,
    get_provider,
)

BG = "#1e1f24"
BG_PANEL = "#26272e"
FG = "#e8e8e8"
FG_DIM = "#9a9aa5"
ACCENT = "#00c2ff"
ERR = "#ff6b6b"

WINDOW_W = 480
WINDOW_H = 330          # 仅 OCR 结果时的高度
TRANS_EXTRA = 140       # 展示翻译结果后增加的高度


class ResultWindow:
    def __init__(self, root: tk.Tk, ocr_engine: OcrEngine, config,
                 on_recapture):
        self._root = root
        self._ocr = ocr_engine
        self._config = config
        self._on_recapture = on_recapture
        self._win: tk.Toplevel | None = None
        self._queue: queue.Queue = queue.Queue()
        self._poll_after_id = None
        self._provider = None
        self._reload_provider()

    # ----------------------------------------------------------------- API

    def _reload_provider(self):
        self._provider = get_provider(self._config.translation)

    def reload_config(self):
        self._reload_provider()

    def is_shown(self) -> bool:
        return self._win is not None

    def hide(self):
        """隐藏浮窗（进入新截图前调用，防止浮窗出现在冻结画面里）。"""
        if self._win is not None:
            try:
                self._win.withdraw()
            except tk.TclError:
                pass

    def show_ocr(self, crop, screen_bbox):
        """对截图运行 OCR 并展示结果。"""
        self._build_window(screen_bbox)
        self._set_status("识别中…")
        threading.Thread(
            target=self._run_ocr, args=(crop,), daemon=True,
            name="ocr-worker").start()

    # ---------------------------------------------------------------- OCR

    def _run_ocr(self, crop):
        try:
            result = self._ocr.recognize(upscale_for_ocr(crop))
            self._queue.put(("ocr_ok", result))
        except OcrError as e:
            self._queue.put(("ocr_err", str(e)))

    # ------------------------------------------------------------ UI build

    def _build_window(self, screen_bbox):
        self.hide()
        if self._win is not None:
            self._destroy_window()

        self._win = tk.Toplevel(self._root)
        self._win.overrideredirect(True)
        self._win.attributes("-topmost", True)
        self._win.configure(bg=BG)
        self._win.geometry(self._compute_geometry(screen_bbox))

        # ---- 标题栏（可拖动）
        bar = tk.Frame(self._win, bg=BG_PANEL)
        bar.pack(fill="x")
        self._drag_data = {"x": 0, "y": 0}
        bar.bind("<Button-1>", self._bar_press)
        bar.bind("<B1-Motion>", self._bar_drag)
        tk.Label(bar, text="● ScreenLens 识别结果", bg=BG_PANEL, fg=FG_DIM,
                 font=("Microsoft YaHei UI", 10, "bold")).pack(side="left",
                                                              padx=10, pady=6)
        btn_close = tk.Label(bar, text="✕", bg=BG_PANEL, fg=FG_DIM,
                             font=("Segoe UI", 11), cursor="hand2")
        btn_close.pack(side="right", padx=10)
        btn_close.bind("<Button-1>", lambda e: self.close())

        # ---- 原文
        body = tk.Frame(self._win, bg=BG)
        body.pack(fill="both", expand=True, padx=10, pady=(6, 4))
        tk.Label(body, text="识别文字（可编辑）", bg=BG, fg=FG_DIM,
                 font=("Microsoft YaHei UI", 9)).pack(anchor="w")
        self._text = tk.Text(body, bg=BG_PANEL, fg=FG, relief="flat",
                             font=("Microsoft YaHei UI", 11), wrap="word",
                             height=6, padx=8, pady=6,
                             insertbackground=FG)
        self._text.pack(fill="both", expand=True)

        # ---- 译文区（翻译后显示）
        self._trans_frame = tk.Frame(self._win, bg=BG)
        self._trans_frame.pack_forget()
        tk.Label(self._trans_frame, text="翻译结果", bg=BG, fg=FG_DIM,
                 font=("Microsoft YaHei UI", 9)).pack(anchor="w", padx=10)
        trans_body = tk.Frame(self._trans_frame, bg=BG)
        trans_body.pack(fill="x", padx=10, pady=(2, 4))
        self._trans_text = tk.Text(trans_body, bg=BG_PANEL, fg=ACCENT,
                                   relief="flat", font=("Microsoft YaHei UI", 11),
                                   wrap="word", height=4, padx=8, pady=6)
        self._trans_text.pack(side="left", fill="both", expand=True)
        sb = tk.Label(trans_body, text="复制", bg=BG_PANEL, fg=FG_DIM,
                      font=("Microsoft YaHei UI", 9), cursor="hand2")
        sb.pack(side="right", anchor="ne", padx=(6, 0), pady=4)
        sb.bind("<Button-1>", lambda e: self._copy_text(
            self._trans_text.get("1.0", "end-1c"), "已复制译文"))

        # ---- 底部工具栏
        toolbar = tk.Frame(self._win, bg=BG)
        toolbar.pack(fill="x", padx=10, pady=(2, 10))

        self._btn_copy = self._make_btn(toolbar, "复制原文", self._on_copy)
        self._btn_copy.pack(side="left")

        self._lang_var = tk.StringVar(
            value=LANG_NAMES.get(self._config.translation.get(
                "target_language", "zh"), "中文"))
        self._btn_translate = self._make_btn(
            toolbar, "翻译", self._on_translate, primary=True)
        self._btn_translate.pack(side="left", padx=(8, 0))
        lang_box = ttk.Combobox(toolbar, textvariable=self._lang_var,
                                values=["中文", "英文", "日文"],
                                state="readonly", width=5, font=(
                                    "Microsoft YaHei UI", 9))
        lang_box.pack(side="left", padx=(6, 14), pady=(3, 0))

        self._btn_re = self._make_btn(toolbar, "重新截图", self._on_recapture_btn)
        self._btn_re.pack(side="right")
        self._btn_close = self._make_btn(toolbar, "关闭", self.close)
        self._btn_close.pack(side="right", padx=(0, 8))

        # ---- 状态栏
        self._status = tk.Label(self._win, text="", bg=BG, fg=FG_DIM,
                                font=("Microsoft YaHei UI", 9), anchor="w")
        self._status.pack(fill="x", padx=12, pady=(0, 6))

        self._win.bind("<Escape>", lambda e: self.close())
        self._win.bind("<FocusOut>", self._on_focus_out)
        self._start_polling()

    def _on_focus_out(self, _e):
        pass  # 失焦不自动关闭，避免误触丢失结果

    def _make_btn(self, parent, text, cmd, primary=False):
        bg = ACCENT if primary else BG_PANEL
        fg = "#08131a" if primary else FG
        btn = tk.Label(parent, text=text, bg=bg, fg=fg, cursor="hand2",
                       font=("Microsoft YaHei UI", 10, "bold"), padx=12,
                       pady=4)
        btn.bind("<Button-1>", lambda e: cmd())
        return btn

    def _compute_geometry(self, screen_bbox):
        try:
            vs = screen_util.virtual_screen_bbox()
            vx0, vy0 = vs["left"], vs["top"]
            vw, vh = vs["width"], vs["height"]
        except Exception:
            vx0, vy0, vw, vh = 0, 0, 1920, 1080
        x0, y0, x1, y1 = screen_bbox
        x, y = x1 + 12, y0
        if x + WINDOW_W > vx0 + vw:
            x = x0 - WINDOW_W - 12
        if x < vx0:
            x = min(max(x0, vx0), vx0 + vw - WINDOW_W)
        if y + WINDOW_H > vy0 + vh:
            y = max(vy0, y1 - WINDOW_H)
        y = max(vy0, min(y, vy0 + vh - WINDOW_H))
        return f"{WINDOW_W}x{WINDOW_H}+{x}+{y}"

    # ------------------------------------------------------------- actions

    def _on_copy(self):
        self._copy_text(self._text.get("1.0", "end-1c"), "已复制到剪贴板")

    def _copy_text(self, text, msg):
        if not text:
            return
        self._root.clipboard_clear()
        self._root.clipboard_append(text)
        self._set_status(msg)

    def _on_recapture_btn(self):
        self.close()
        self._on_recapture()

    def _on_translate(self):
        text = self._text.get("1.0", "end-1c").strip()
        if not text:
            self._set_status("没有可翻译的文字。", error=True)
            return
        lang_name = self._lang_var.get()
        target = next((k for k, v in LANG_NAMES.items() if v == lang_name),
                      "zh")
        provider = self._provider
        self._set_status(
            f"翻译中…（文本将发送至在线服务：{provider.name}）")
        self._btn_translate.configure(fg=FG_DIM)

        def work():
            try:
                result = provider.translate(text, target)
                self._queue.put(("trans_ok", result))
            except TranslationError as e:
                self._queue.put(("trans_err", str(e)))

        threading.Thread(target=work, daemon=True,
                         name="translate-worker").start()

    # ------------------------------------------------------- queue polling

    def _start_polling(self):
        self._poll_after_id = self._win.after(60, self._poll_queue)

    def _poll_queue(self):
        try:
            while True:
                kind, payload = self._queue.get_nowait()
                if kind == "ocr_ok":
                    self._handle_ocr(payload)
                elif kind == "ocr_err":
                    self._handle_ocr_error(payload)
                elif kind == "trans_ok":
                    self._handle_translation(payload)
                elif kind == "trans_err":
                    self._set_status(payload, error=True)
        except queue.Empty:
            pass
        if self._win is not None:
            self._poll_after_id = self._win.after(60, self._poll_queue)

    def _handle_ocr(self, result):
        if self._win is None:
            return
        if result.is_empty:
            self._text.delete("1.0", "end")
            self._text.insert("1.0", "未识别到文字")
            self._text.configure(fg=FG_DIM)
            self._btn_copy.configure(fg=FG_DIM)
            self._btn_translate.configure(fg=FG_DIM)
            self._set_status("未识别到文字，可点击“重新截图”再试。")
            return
        self._text.delete("1.0", "end")
        self._text.insert("1.0", result.text)
        avg = sum(result.scores) / len(result.scores) if result.scores else 0
        self._set_status(f"识别完成 · {len(result.lines)} 行 · "
                         f"平均置信度 {avg:.0%}")

    def _handle_ocr_error(self, msg):
        if self._win is None:
            return
        self._text.delete("1.0", "end")
        self._text.insert("1.0", "未识别到文字")
        self._text.configure(fg=FG_DIM)
        self._set_status(msg, error=True)

    def _handle_translation(self, result):
        if self._win is None:
            return
        self._btn_translate.configure(fg="#08131a")
        self._trans_text.delete("1.0", "end")
        self._trans_text.insert("1.0", result)
        self._trans_frame.pack(fill="x", before=self._status)
        # 窗口加高以容纳译文
        x, y = self._win.winfo_x(), self._win.winfo_y()
        try:
            vs = screen_util.virtual_screen_bbox()
            y = max(vs["top"], min(y, vs["top"] + vs["height"]
                                   - (WINDOW_H + TRANS_EXTRA)))
        except Exception:
            pass
        self._win.geometry(f"{WINDOW_W}x{WINDOW_H + TRANS_EXTRA}+{x}+{y}")
        self._set_status("翻译完成")

    # -------------------------------------------------------------- helper

    def _set_status(self, msg, error=False):
        if self._status is not None and self._win is not None:
            self._status.configure(text=msg, fg=ERR if error else FG_DIM)

    def _bar_press(self, event):
        self._drag_data["x"] = event.x
        self._drag_data["y"] = event.y

    def _bar_drag(self, event):
        if self._win is None:
            return
        x = self._win.winfo_x() + event.x - self._drag_data["x"]
        y = self._win.winfo_y() + event.y - self._drag_data["y"]
        self._win.geometry(f"+{x}+{y}")

    def _destroy_window(self):
        if self._poll_after_id is not None:
            try:
                self._root.after_cancel(self._poll_after_id)
            except Exception:
                pass
            self._poll_after_id = None
        if self._win is not None:
            try:
                self._win.destroy()
            except tk.TclError:
                pass
            self._win = None

    def close(self):
        self._destroy_window()
