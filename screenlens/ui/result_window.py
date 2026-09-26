# -*- coding: utf-8 -*-
"""OCR 结果面板：层级化的原文 / 译文展示（第三阶段 UI 改版）。

布局层级（自上而下）：
1. 标题栏：状态指示（识别中/完成/翻译中…）+ 关闭按钮，可拖动
2. 识别文字卡片：可编辑，独立滚动
3. 翻译卡片：翻译后出现，独立滚动，卡片头部有「复制译文」
4. 工具栏：复制（主操作）、翻译 + 目标语言（相邻）、重新截图、关闭
5. 状态栏：语义化提示（info/busy/success/error，图标+颜色）

线程模型：
- OCR 与翻译在工作线程执行，结果通过 queue 投递，
  由主线程 _poll_queue 统一消费；
- 每次新任务（show_ocr / 重新翻译）携带递增的 task id，
  旧任务的迟到结果会被丢弃，不会覆盖新界面；
- 翻译请求进行中禁止重复提交，所有结束路径都会恢复按钮状态。

隐私：OCR 完全本地；仅当用户点击"翻译"时，
识别文本（而非截图）会发送给所选在线服务，状态栏会明确提示。
"""
import gc
import logging
import queue
import threading
import tkinter as tk
import tkinter.ttk as ttk

from screenlens.capture import screen as screen_util
from screenlens.capture.region import upscale_for_ocr
from screenlens.ocr.engine import OcrError
from screenlens.translate.provider import (
    LANG_NAMES,
    TranslationError,
    get_provider,
)
from screenlens.ui.theme import DARK as PAL, F_BODY, F_SMALL, F_TITLE, \
    FlatButton, StatusLabel

logger = logging.getLogger(__name__)

WINDOW_W = 480
WINDOW_H = 350          # 仅 OCR 结果时的高度
TRANS_EXTRA = 150       # 展示翻译结果后增加的高度


class _Chip(tk.Label):
    """标题栏状态指示（图标 + 文字，不只靠颜色）。"""

    _STYLES = {
        "busy":    ("… 识别中", PAL["fg_dim"]),
        "done":    ("✓ 识别完成", PAL["success"]),
        "trans":   ("… 翻译中", PAL["accent"]),
        "transdone": ("✓ 翻译完成", PAL["accent"]),
    }

    def __init__(self, parent):
        super().__init__(parent, text="", bg=PAL["bg_card"],
                         font=("Microsoft YaHei UI", 9))
        self.set_state("busy")

    def set_state(self, key: str):
        text, fg = self._STYLES.get(key, ("", PAL["fg_dim"]))
        self.configure(text=text, fg=fg)


class ResultWindow:
    def __init__(self, root: tk.Tk, ocr_engine, config, on_recapture):
        self._root = root
        self._ocr = ocr_engine
        self._config = config
        self._on_recapture = on_recapture
        self._win: tk.Toplevel | None = None
        self._queue: queue.Queue = queue.Queue()
        self._poll_after_id = None
        self._provider = None
        self._task_gen = 0            # 任务代际号：新任务递增，旧结果作废
        self._translating = False    # 翻译请求进行中（防重复提交）
        self._has_text = False       # 当前是否有可翻译的识别文本
        self._reload_provider()

    # ----------------------------------------------------------------- API

    def _reload_provider(self):
        self._provider = get_provider(self._config.translation)

    def reload_config(self):
        """设置保存后调用：立即应用新的翻译配置。"""
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
        """对截图运行 OCR 并展示结果（主线程调用）。"""
        self._build_window(screen_bbox)
        self._set_status("识别中…", "busy")
        task = self._task_gen
        threading.Thread(
            target=self._run_ocr, args=(task, crop), daemon=True,
            name="ocr-worker").start()

    # ---------------------------------------------------------------- OCR

    def _run_ocr(self, task: int, crop):
        """工作线程：识别并投递结果。"""
        try:
            result = self._ocr.recognize(upscale_for_ocr(crop))
            self._queue.put({"task": task, "kind": "ocr_ok",
                             "payload": result})
        except OcrError as e:
            self._queue.put({"task": task, "kind": "ocr_err",
                             "payload": str(e)})
        except Exception as e:  # 未预期异常也要反馈，不让浮窗卡在"识别中"
            logger.exception("unexpected OCR worker error (%s)",
                             type(e).__name__)
            self._queue.put({"task": task, "kind": "ocr_err",
                            "payload": "识别失败，请重试或重新截图。"})

    # ------------------------------------------------------------ UI build

    def _build_window(self, screen_bbox):
        self.hide()
        self._destroy_window()
        # 新一代任务：旧任务的所有排队结果作废
        self._task_gen += 1
        self._translating = False
        self._has_text = False

        self._win = tk.Toplevel(self._root)
        self._win.overrideredirect(True)
        self._win.attributes("-topmost", True)
        self._win.configure(bg=PAL["bg"])
        self._win.geometry(self._compute_geometry(screen_bbox))

        # ---- 标题栏（状态指示 + 可拖动 + 关闭）
        bar = tk.Frame(self._win, bg=PAL["bg_card"],
                       highlightthickness=1,
                       highlightbackground=PAL["border"])
        bar.pack(fill="x")
        self._drag_data = {"x": 0, "y": 0}
        bar.bind("<Button-1>", self._bar_press)
        bar.bind("<B1-Motion>", self._bar_drag)
        tk.Label(bar, text="ScreenLens", bg=PAL["bg_card"], fg=PAL["fg"],
                 font=F_TITLE).pack(side="left", padx=(12, 10), pady=8)
        self._chip = _Chip(bar)
        self._chip.pack(side="left", pady=8)
        self._btn_close_title = FlatButton(
            bar, "✕", self.close, PAL, kind="secondary",
            font=("Segoe UI", 11), padx=10, pady=3)
        self._btn_close_title.pack(side="right", padx=6, pady=4)

        # ---- 识别文字卡片
        body = tk.Frame(self._win, bg=PAL["bg"])
        body.pack(fill="both", expand=True, padx=12, pady=(10, 4))
        tk.Label(body, text="识别文字（可编辑）", bg=PAL["bg"],
                 fg=PAL["fg_dim"], font=F_SMALL).pack(anchor="w")
        text_row = tk.Frame(body, bg=PAL["bg"])
        text_row.pack(fill="both", expand=True)
        self._text = tk.Text(text_row, bg=PAL["bg_input"], fg=PAL["fg"],
                             relief="flat", font=F_BODY, wrap="word",
                             height=7, padx=10, pady=8,
                             insertbackground=PAL["fg"],
                             highlightthickness=1,
                             highlightbackground=PAL["border"],
                             highlightcolor=PAL["accent"])
        self._text.pack(side="left", fill="both", expand=True)
        scrollbar = tk.Scrollbar(text_row, command=self._text.yview,
                                  width=10, bg=PAL["bg_input"],
                                  activebackground=PAL["fg_dim"],
                                  troughcolor=PAL["bg_input"])
        scrollbar.pack(side="right", fill="y")
        self._text.configure(yscrollcommand=scrollbar.set)

        # ---- 工具栏（先建，译文卡片需要插到它上方）
        toolbar = tk.Frame(self._win, bg=PAL["bg"])
        self._toolbar = toolbar
        toolbar.pack(fill="x", padx=12, pady=(2, 4))

        self._btn_copy = FlatButton(toolbar, "⧉ 复制", self._on_copy, PAL,
                                    kind="primary")
        self._btn_copy.pack(side="left")
        self._btn_copy.set_enabled(False)

        self._btn_re = FlatButton(toolbar, "⟳ 重新截图",
                                  self._on_recapture_btn, PAL)
        self._btn_re.pack(side="right", padx=(10, 0))

        self._lang_var = tk.StringVar(
            value=LANG_NAMES.get(self._config.translation.get(
                "target_language", "zh"), "中文"))
        lang_box = ttk.Combobox(toolbar, textvariable=self._lang_var,
                                values=["中文", "英文", "日文"],
                                state="readonly", width=5, font=(
                                    "Microsoft YaHei UI", 9))
        lang_box.pack(side="right", padx=(6, 0))
        self._btn_translate = FlatButton(
            toolbar, "翻译 ▸", self._on_translate, PAL)
        self._btn_translate.pack(side="right")
        self._btn_translate.set_enabled(False)

        # ---- 译文卡片（翻译后显示，位于工具栏上方）
        self._trans_frame = tk.Frame(self._win, bg=PAL["bg"])
        self._trans_frame.pack_forget()
        trans_head = tk.Frame(self._trans_frame, bg=PAL["bg"])
        trans_head.pack(fill="x", padx=12)
        tk.Label(trans_head, text="翻译结果", bg=PAL["bg"],
                 fg=PAL["fg_dim"], font=F_SMALL).pack(side="left")
        self._btn_copy_trans = FlatButton(
            trans_head, "复制译文", self._on_copy_trans, PAL,
            font=F_SMALL, padx=10, pady=2)
        self._btn_copy_trans.pack(side="right")
        trans_body = tk.Frame(self._trans_frame, bg=PAL["bg"])
        trans_body.pack(fill="both", expand=True, padx=12, pady=(2, 4))
        self._trans_text = tk.Text(trans_body, bg=PAL["bg_input"],
                                   fg=PAL["fg"], relief="flat",
                                   font=F_BODY, wrap="word", height=5,
                                   padx=10, pady=8,
                                   insertbackground=PAL["fg"],
                                   highlightthickness=1,
                                   highlightbackground=PAL["border"])
        self._trans_text.pack(side="left", fill="both", expand=True)
        tsb = tk.Scrollbar(trans_body, command=self._trans_text.yview,
                           width=10, bg=PAL["bg_input"],
                           activebackground=PAL["fg_dim"],
                           troughcolor=PAL["bg_input"])
        tsb.pack(side="right", fill="y")
        self._trans_text.configure(yscrollcommand=tsb.set)

        # ---- 状态栏
        self._status = StatusLabel(self._win, PAL, bg=PAL["bg"])
        self._status.pack(fill="x", padx=14, pady=(0, 8))

        self._win.bind("<Escape>", lambda e: self.close())
        self._start_polling()

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
        if not self._has_text:
            self._set_status("没有可复制的文字。", "error")
            return
        self._copy_text(self._text.get("1.0", "end-1c"), "已复制到剪贴板")

    def _on_copy_trans(self):
        text = self._trans_text.get("1.0", "end-1c")
        if not text:
            self._set_status("暂无译文。", "error")
            return
        self._copy_text(text, "已复制译文")

    def _copy_text(self, text, msg):
        if not text:
            return
        try:
            self._root.clipboard_clear()
            self._root.clipboard_append(text)
            # 校验剪贴板内容确实写入成功
            if self._root.clipboard_get() != text:
                raise tk.TclError("clipboard verify failed")
        except tk.TclError:
            self._set_status("复制失败，请重试。", "error")
            return
        self._set_status(msg, "success")

    def _on_recapture_btn(self):
        self.close()
        self._on_recapture()

    # ------------------------------------------------------------- 翻译

    def _provider_hint(self, provider) -> str | None:
        """根据 Provider 返回隐私提示；本地/未启用返回 None。"""
        name = provider.name
        if name == "none":
            return None
        if name == "openai":
            base = getattr(provider, "base_url", "")
            host = base.split("//", 1)[-1].split("/", 1)[0] if base else "服务"
            return f"仅识别文本将发送至 {host}，截图不会上传"
        if name == "google_free":
            return "仅识别文本将发送至 Google 翻译接口，截图不会上传"
        return "仅识别文本将发送至在线服务，截图不会上传"

    def _on_translate(self):
        if self._translating:
            self._set_status("翻译进行中，请稍候…", "busy")
            return
        if not self._has_text:
            self._set_status("没有可翻译的文字。", "error")
            return

        provider = self._provider
        # 1) 未启用翻译
        if provider.name == "none":
            self._set_status(
                "翻译未启用：请在托盘图标右键菜单打开「设置」，"
                "选择翻译服务。", "error")
            return
        # 2) OpenAI Provider 未配置
        if provider.name == "openai" and not getattr(
                provider, "is_configured", True):
            self._set_status(
                "当前未配置在线翻译服务：请在托盘菜单打开「设置」，"
                "填写 OpenAI 兼容接口的 Base URL 与 API Key。", "error")
            return

        text = self._text.get("1.0", "end-1c").strip()
        if not text:
            self._set_status("没有可翻译的文字。", "error")
            return
        lang_name = self._lang_var.get()
        target = next((k for k, v in LANG_NAMES.items() if v == lang_name),
                      "zh")

        hint = self._provider_hint(provider)
        self._set_status(f"翻译中…（{hint}）", "busy")
        self._chip.set_state("trans")
        self._set_translate_busy(True)
        task = self._task_gen

        def work():
            try:
                result = provider.translate(text, target)
                self._queue.put({"task": task, "kind": "trans_ok",
                                "payload": result})
            except TranslationError as e:
                self._queue.put({"task": task, "kind": "trans_err",
                                 "payload": str(e)})
            except Exception as e:
                logger.exception("unexpected translate worker error (%s)",
                                 type(e).__name__)
                self._queue.put({
                    "task": task, "kind": "trans_err",
                    "payload": "翻译失败，请检查网络或服务配置。"})

        threading.Thread(target=work, daemon=True,
                         name="translate-worker").start()

    def _set_translate_busy(self, busy: bool):
        """翻译请求进行中禁用按钮，结束后恢复。"""
        self._translating = busy
        btn = getattr(self, "_btn_translate", None)
        if btn is not None and self._win is not None:
            btn.set_enabled(not busy and self._has_text)

    # ------------------------------------------------------- queue polling

    def _start_polling(self):
        self._poll_after_id = self._win.after(60, self._poll_queue)

    def _poll_queue(self):
        try:
            while True:
                msg = self._queue.get_nowait()
                if msg.get("task") != self._task_gen:
                    continue  # 迟到的旧任务结果，直接丢弃
                kind = msg.get("kind")
                payload = msg.get("payload")
                if kind == "ocr_ok":
                    self._handle_ocr(payload)
                elif kind == "ocr_err":
                    self._handle_ocr_error(payload)
                elif kind == "trans_ok":
                    self._handle_translation(payload)
                elif kind == "trans_err":
                    self._set_translate_busy(False)
                    self._chip.set_state("done")
                    self._set_status(payload, "error")
        except queue.Empty:
            pass
        if self._win is not None:
            self._poll_after_id = self._win.after(60, self._poll_queue)

    def _handle_ocr(self, result):
        if self._win is None:
            return
        self._chip.set_state("done")
        if result.is_empty:
            self._text.delete("1.0", "end")
            self._text.insert("1.0", "未识别到文字")
            self._text.configure(fg=PAL["fg_dim"])
            self._set_status("未识别到文字，可点击「重新截图」再试。",
                             "info")
            return
        self._has_text = True
        self._text.delete("1.0", "end")
        self._text.insert("1.0", result.text)
        self._btn_copy.set_enabled(True)
        self._btn_translate.set_enabled(True)
        avg = sum(result.scores) / len(result.scores) if result.scores else 0
        self._set_status(f"识别完成 · {len(result.lines)} 行 · "
                         f"平均置信度 {avg:.0%}", "success")

    def _handle_ocr_error(self, msg):
        if self._win is None:
            return
        self._chip.set_state("done")
        self._text.delete("1.0", "end")
        self._text.insert("1.0", "未识别到文字")
        self._text.configure(fg=PAL["fg_dim"])
        self._set_status(msg, "error")

    def _handle_translation(self, result):
        if self._win is None:
            return
        self._set_translate_busy(False)
        self._chip.set_state("transdone")
        self._trans_text.delete("1.0", "end")
        self._trans_text.insert("1.0", result)
        self._trans_frame.pack(fill="both", expand=True,
                               before=self._toolbar)
        # 窗口加高以容纳译文，并限制在虚拟桌面内
        x, y = self._win.winfo_x(), self._win.winfo_y()
        try:
            vs = screen_util.virtual_screen_bbox()
            y = max(vs["top"], min(y, vs["top"] + vs["height"]
                                   - (WINDOW_H + TRANS_EXTRA)))
        except Exception:
            pass
        self._win.geometry(
            f"{WINDOW_W}x{WINDOW_H + TRANS_EXTRA}+{x}+{y}")
        self._set_status("翻译完成，可点击译文卡片中的「复制译文」",
                         "success")

    # -------------------------------------------------------------- helper

    def _set_status(self, msg, level="info"):
        """level: info / busy / success / error"""
        if self._status is not None and self._win is not None:
            self._status.set_status(msg, level)

    def _bar_press(self, event):
        self._drag_data["x"] = event.x
        self._drag_data["y"] = event.y

    def _bar_drag(self, event):
        if self._win is None:
            return
        x = self._win.winfo_x() + event.x - self._drag_data["x"]
        y = self._win.winfo_y() + event.y - self._drag_data["y"]
        # 拖动不允许跑出虚拟桌面边界
        try:
            vs = screen_util.virtual_screen_bbox()
            x = max(vs["left"], min(x, vs["left"] + vs["width"] - WINDOW_W))
            y = max(vs["top"], min(y, vs["top"] + vs["height"] - WINDOW_H))
        except Exception:
            pass
        self._win.geometry(f"+{x}+{y}")

    def _destroy_window(self):
        # 使旧任务结果作废；取消轮询；销毁窗口
        self._task_gen += 1
        self._translating = False
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
        # 主线程立即回收控件循环引用，防止工作线程 GC 触发
        # 跨线程 Tcl 调用（Tcl_AsyncDelete 崩溃）
        gc.collect()

    def close(self):
        self._destroy_window()
