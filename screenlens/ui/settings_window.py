# -*- coding: utf-8 -*-
"""ScreenLens 设置窗口（Tkinter 轻量实现）。

功能：
- 快捷键查看 / 录入修改（捕获按键组合，保存前校验格式）
- 翻译 Provider 选择：Google 免费接口 / OpenAI 兼容接口 / 关闭翻译
- OpenAI 兼容配置：Base URL、API Key（默认隐藏）、模型
- 默认目标语言：中文 / 英文 / 日文
- 隐私说明：OCR 完全本地；在线 Provider 仅在点击翻译时发送识别文本
- 保存（立即生效）、取消（不保存）、恢复默认值

保存时由 apply_callback 执行实际应用（写配置、重注册热键、
刷新 Provider）。热键注册失败会回滚到先前可用的快捷键并向用户说明。

与 app 解耦以便测试：apply_callback(hotkey, translation) -> list[str]，
返回空列表表示成功，否则为错误信息列表。
"""
import gc
import logging
import re
import tkinter as tk
from tkinter import messagebox
import tkinter.ttk as ttk

from screenlens.config import DEFAULT_CONFIG, validate_config

logger = logging.getLogger(__name__)

BG = "#1e1f24"
BG_PANEL = "#26272e"
FG = "#e8e8e8"
FG_DIM = "#9a9aa5"
ACCENT = "#00c2ff"

PROVIDER_LABELS = [
    ("google_free", "Google 免费翻译（无需 API Key，需联网）"),
    ("openai", "OpenAI 兼容接口（OpenAI / DeepSeek / Ollama 等）"),
    ("none", "关闭翻译（完全离线）"),
]
LANG_ITEMS = ["中文", "英文", "日文"]
LANG_CODES = {"中文": "zh", "英文": "en", "日文": "ja"}
PRIVACY_NOTE = (
    "隐私说明：截图与 OCR 识别完全在本机完成，不上传任何图片。\n"
    "仅当你在识别结果窗口点击「翻译」时，识别出的文字（而非截图）"
    "会发送给上面选择的在线翻译服务。关闭翻译即完全离线。"
)

# 修饰键的 Tk event.state 位掩码（Windows）
_STATE_SHIFT = 0x0001
_STATE_CTRL = 0x0004
_STATE_ALT = 0x0008
_MODIFIER_KEYSYMS = {
    "shift_l", "shift_r", "control_l", "control_r",
    "alt_l", "alt_r", "win_l", "win_r", "caps_lock", "num_lock",
    "escape",
}


def hotkey_from_event(event) -> tuple[str | None, str | None]:
    """把 Tk KeyPress 事件转换为 keyboard 库格式的快捷键字符串。

    返回 (hotkey, error)。纯修饰键按住或组合不完整时返回 (None, 原因)。
    Escape 单独按下视为取消录入，返回 (None, None)。
    """
    keysym = (event.keysym or "").lower()
    if keysym == "escape":
        return None, None

    mods = []
    if event.state & _STATE_CTRL:
        mods.append("ctrl")
    if event.state & _STATE_ALT:
        mods.append("alt")
    if event.state & _STATE_SHIFT:
        mods.append("shift")

    if keysym in _MODIFIER_KEYSYMS:
        return None, "请继续按住修饰键并再按一个普通按键"

    # 校验按键名合法（字母/数字/功能键/标点等 keysym 形式）
    if not keysym or not keysym.replace("_", "").isalnum():
        return None, f"无法识别的按键：{event.keysym}"

    # 无修饰键时只允许独立功能键（f1-f24 / print_screen 等）
    lone_ok = bool(re.fullmatch(r"f\d{1,2}", keysym)) or keysym in (
        "print_screen", "pause", "scroll_lock")
    if not mods and not lone_ok:
        return None, "普通按键需要配合 Ctrl / Alt / Shift 使用"

    combo = "+".join(mods + [keysym])
    # keyboard 库格式校验
    try:
        import keyboard

        keyboard.parse_hotkey(combo)
    except Exception:
        return None, f"快捷键格式不正确：{combo}"
    return combo, None


class SettingsWindow:
    def __init__(self, root: tk.Tk, config, apply_callback):
        self._root = root
        self._config = config
        self._apply = apply_callback
        self._win: tk.Toplevel | None = None
        self._capturing = False

    # ---------------------------------------------------------------- API

    def show(self):
        if self._win is not None and self._win.winfo_exists():
            self._win.deiconify()
            self._win.focus_force()
            return
        self._build()
        self._win.protocol("WM_DELETE_WINDOW", self._close)

    def is_shown(self) -> bool:
        return self._win is not None and self._win.winfo_exists()

    # -------------------------------------------------------------- build

    def _build(self):
        tr = self._config.translation
        self._win = tk.Toplevel(self._root)
        self._win.title("ScreenLens 设置")
        self._win.configure(bg=BG, padx=0, pady=0)
        self._win.resizable(False, False)
        self._win.attributes("-topmost", True)
        self._topmost_after_id = self._win.after(200, self._unset_topmost)

        pad = {"padx": 16, "pady": (6, 2)}

        # ---- 快捷键 ----
        frame_hk = tk.LabelFrame(self._win, text=" 全局快捷键 ", bg=BG,
                                 fg=FG_DIM,
                                 font=("Microsoft YaHei UI", 10, "bold"))
        frame_hk.pack(fill="x", **pad)
        row = tk.Frame(frame_hk, bg=BG)
        row.pack(fill="x", padx=12, pady=10)
        self._hotkey_var = tk.StringVar(value=self._config.hotkey)
        self._hotkey_entry = tk.Entry(row, textvariable=self._hotkey_var,
                                      state="readonly", width=24, bg=BG_PANEL,
                                      fg=FG, relief="flat",
                                      font=("Consolas", 11))
        self._hotkey_entry.pack(side="left", ipady=4)
        self._btn_capture = tk.Label(row, text=" 录入新快捷键 ", bg=BG_PANEL,
                                     fg=FG, cursor="hand2", padx=10, pady=4,
                                     font=("Microsoft YaHei UI", 10))
        self._btn_capture.pack(side="left", padx=(10, 0))
        self._btn_capture.bind("<Button-1>", lambda e: self._start_capture())
        self._hotkey_hint = tk.Label(frame_hk, bg=BG, fg=FG_DIM,
                                     font=("Microsoft YaHei UI", 9),
                                     text="点击「录入新快捷键」后按下组合键，"
                                          "Esc 取消录入")
        self._hotkey_hint.pack(anchor="w", padx=12, pady=(0, 8))

        # ---- 翻译服务 ----
        frame_tr = tk.LabelFrame(self._win, text=" 翻译服务 ", bg=BG,
                                 fg=FG_DIM,
                                 font=("Microsoft YaHei UI", 10, "bold"))
        frame_tr.pack(fill="x", **pad)
        self._provider_var = tk.StringVar(
            value=tr.get("provider", "google_free"))
        for value, label in PROVIDER_LABELS:
            tk.Radiobutton(frame_tr, text=label, variable=self._provider_var,
                           value=value, bg=BG, fg=FG, selectcolor=BG_PANEL,
                           activebackground=BG, activeforeground=FG,
                           font=("Microsoft YaHei UI", 10),
                           command=self._on_provider_change
                           ).pack(anchor="w", padx=12, pady=(8, 0))

        # OpenAI 参数
        self._oa_frame = tk.Frame(frame_tr, bg=BG)
        self._oa_frame.pack(fill="x", padx=28, pady=(6, 4))
        oa = tr.get("openai", {})
        self._url_var = tk.StringVar(value=oa.get("base_url", ""))
        self._key_var = tk.StringVar(value=oa.get("api_key", ""))
        self._model_var = tk.StringVar(value=oa.get("model", ""))
        self._show_key = tk.BooleanVar(value=False)

        def add_field(parent, label, var, **kw):
            r = tk.Frame(parent, bg=BG)
            r.pack(fill="x", pady=3)
            tk.Label(r, text=label, bg=BG, fg=FG_DIM, width=10, anchor="w",
                     font=("Microsoft YaHei UI", 9)).pack(side="left")
            e = tk.Entry(r, textvariable=var, bg=BG_PANEL, fg=FG,
                         relief="flat", width=34,
                         font=("Microsoft YaHei UI", 10), **kw)
            e.pack(side="left", ipady=3)
            return e

        self._url_entry = add_field(self._oa_frame, "Base URL",
                                    self._url_var)
        self._key_entry = add_field(self._oa_frame, "API Key",
                                    self._key_var, show="*")
        tk.Checkbutton(self._oa_frame, text="显示 API Key",
                       variable=self._show_key, bg=BG, fg=FG_DIM,
                       selectcolor=BG_PANEL, activebackground=BG,
                       font=("Microsoft YaHei UI", 9),
                       command=self._toggle_key_visibility
                       ).pack(anchor="w", pady=(2, 0))
        self._model_entry = add_field(self._oa_frame, "模型",
                                      self._model_var)
        self._on_provider_change()

        # ---- 目标语言 ----
        frame_lang = tk.LabelFrame(self._win, text=" 默认目标语言 ", bg=BG,
                                   fg=FG_DIM,
                                   font=("Microsoft YaHei UI", 10, "bold"))
        frame_lang.pack(fill="x", **pad)
        self._lang_var = tk.StringVar(
            value={"zh": "中文", "en": "英文", "ja": "日文"}.get(
                tr.get("target_language", "zh"), "中文"))
        ttk.Combobox(frame_lang, textvariable=self._lang_var,
                     values=LANG_ITEMS, state="readonly", width=8,
                     font=("Microsoft YaHei UI", 10)).pack(
            anchor="w", padx=12, pady=10)

        # ---- 隐私说明 ----
        frame_priv = tk.LabelFrame(self._win, text=" 隐私 ", bg=BG,
                                   fg=FG_DIM,
                                   font=("Microsoft YaHei UI", 10, "bold"))
        frame_priv.pack(fill="x", **pad)
        tk.Label(frame_priv, text=PRIVACY_NOTE, bg=BG, fg=FG_DIM,
                 justify="left", wraplength=430,
                 font=("Microsoft YaHei UI", 9)).pack(
            anchor="w", padx=12, pady=10)

        # ---- 底部按钮 ----
        btns = tk.Frame(self._win, bg=BG)
        btns.pack(fill="x", padx=16, pady=(10, 16))

        def make_btn(parent, text, cmd, primary=False):
            bg = ACCENT if primary else BG_PANEL
            fg = "#08131a" if primary else FG
            b = tk.Label(parent, text=text, bg=bg, fg=fg, cursor="hand2",
                         padx=16, pady=5,
                         font=("Microsoft YaHei UI", 10, "bold"))
            b.bind("<Button-1>", lambda e: cmd())
            return b

        make_btn(btns, "保存并生效", self._on_save, primary=True).pack(
            side="right")
        make_btn(btns, "取消", self._close).pack(side="right", padx=(0, 10))
        make_btn(btns, "恢复默认值", self._on_reset_defaults).pack(
            side="left")

    # ------------------------------------------------------- hotkey capture

    def _start_capture(self):
        self._capturing = True
        self._btn_capture.configure(text=" 请按下组合键（Esc 取消）… ",
                                    fg=ACCENT)
        self._win.bind("<KeyPress>", self._on_capture_key)
        self._win.focus_force()

    def _on_capture_key(self, event):
        hotkey, error = hotkey_from_event(event)
        if hotkey is None and error is None:
            self._stop_capture()          # Esc 取消
            return
        if hotkey is None:
            self._hotkey_hint.configure(
                text=f"{error}（Esc 取消录入）", fg="#ff6b6b")
            return
        self._hotkey_var.set(hotkey)
        self._hotkey_hint.configure(
            text=f"已录入：{hotkey}（保存后生效）", fg=FG_DIM)
        self._stop_capture()

    def _stop_capture(self):
        self._capturing = False
        self._win.unbind("<KeyPress>")
        self._btn_capture.configure(text=" 录入新快捷键 ", fg=FG)

    # ------------------------------------------------------------- actions

    def _unset_topmost(self):
        self._topmost_after_id = None
        if self._win is not None and self._win.winfo_exists():
            try:
                self._win.attributes("-topmost", False)
            except tk.TclError:
                pass

    def _on_provider_change(self):
        if self._provider_var.get() == "openai":
            for child in self._oa_frame.winfo_children():
                try:
                    child.configure(state="normal")
                except tk.TclError:
                    pass
        else:
            for child in self._oa_frame.winfo_children():
                try:
                    child.configure(state="disabled")
                except tk.TclError:
                    pass

    def _toggle_key_visibility(self):
        self._key_entry.configure(
            show="" if self._show_key.get() else "*")

    def _collect(self) -> dict:
        lang_name = self._lang_var.get()
        provider = self._provider_var.get()
        return {
            "hotkey": self._hotkey_var.get().strip(),
            "translation": {
                "provider": provider,
                "target_language": LANG_CODES.get(lang_name, "zh"),
                "openai": {
                    "base_url": self._url_var.get().strip(),
                    "api_key": self._key_var.get().strip(),
                    "model": self._model_var.get().strip()
                    or DEFAULT_CONFIG["translation"]["openai"]["model"],
                },
            },
        }

    def _on_save(self):
        candidate = self._collect()

        # 1) 结构校验（本地，不触碰任何系统状态）
        errors = validate_config(candidate)
        if errors:
            messagebox.showerror(
                "设置无效", "以下问题需要修正：\n\n• " + "\n• ".join(errors),
                parent=self._win)
            return

        # 2) 交给应用层应用（写配置 + 注册热键 + 刷新 Provider）
        #    失败时应用层负责回滚，并返回错误说明
        apply_errors = self._apply(candidate["hotkey"],
                                   candidate["translation"])
        if apply_errors:
            messagebox.showwarning(
                "部分设置未生效",
                "以下问题导致部分设置未生效：\n\n• " + "\n• ".join(
                    apply_errors) + "\n\n其余设置已保存。快捷键仍为当前"
                "可用的值。",
                parent=self._win)
            # 显示当前实际生效的快捷键
            self._hotkey_var.set(self._config.hotkey)
            return

        messagebox.showinfo("设置", "设置已保存并立即生效。", parent=self._win)
        self._close()

    def _on_reset_defaults(self):
        """恢复默认值（仅填充界面，不保存）。"""
        self._hotkey_var.set(DEFAULT_CONFIG["hotkey"])
        self._provider_var.set(DEFAULT_CONFIG["translation"]["provider"])
        self._lang_var.set({"zh": "中文", "en": "英文",
                            "ja": "日文"}[
                                DEFAULT_CONFIG["translation"][
                                    "target_language"]])
        self._url_var.set(DEFAULT_CONFIG["translation"]["openai"]["base_url"])
        self._key_var.set("")
        self._model_var.set(DEFAULT_CONFIG["translation"]["openai"]["model"])
        self._on_provider_change()

    def _close(self):
        if self._win is None:
            return
        after_id = getattr(self, "_topmost_after_id", None)
        if after_id is not None:
            try:
                self._win.after_cancel(after_id)
            except Exception:
                pass
            self._topmost_after_id = None
        try:
            self._win.unbind("<KeyPress>")
            self._win.destroy()
        except tk.TclError:
            pass
        self._win = None
        self._capturing = False
        # 关键：销毁后在主线程立即回收控件循环引用，
        # 防止后续工作线程 GC 触发跨线程 Tcl 调用导致崩溃
        gc.collect()
