# -*- coding: utf-8 -*-
"""ScreenLens 设置窗口（第三阶段 UI 改版：分组卡片布局）。

布局（自上而下的分组卡片）：
1. 快捷键：当前热键展示 + 录入新快捷键
2. 翻译服务：Provider 选择（仅显示所选 Provider 的相关字段）、
   目标语言；API Key 默认隐藏
3. 隐私：本地 OCR / 仅发送识别文本的说明
4. 底部操作行：恢复默认值 / 取消 / 保存并生效 + 内联保存反馈

保存成功/失败都在当前窗口内联给出明确反馈（不依赖系统弹窗），
成功后短暂停留再关闭。

保存时由 apply_callback 执行实际应用（写配置、重注册热键、
刷新 Provider）。热键注册失败会回滚到先前可用的快捷键并向用户说明。

与 app 解耦以便测试：apply_callback(hotkey, translation) -> list[str]，
返回空列表表示成功，否则为错误信息列表。
"""
import gc
import logging
import re
import tkinter as tk
from tkinter import messagebox  # noqa: F401  （测试桩会替换该属性）
import tkinter.ttk as ttk

from screenlens.config import DEFAULT_CONFIG, validate_config
from screenlens.ui.theme import (
    F_BODY,
    F_SMALL,
    F_TITLE,
    GAP,
    PAD,
    PAD_LG,
    FlatButton,
    StatusLabel,
    card,
    palette_for_settings,
)

logger = logging.getLogger(__name__)

PROVIDER_LABELS = [
    ("google_free", "Google 免费翻译（无需 API Key，需联网）"),
    ("openai", "OpenAI 兼容接口（OpenAI / DeepSeek / Ollama 等）"),
    ("none", "关闭翻译（完全离线）"),
]
LANG_ITEMS = ["中文", "英文", "日文"]
LANG_CODES = {"中文": "zh", "英文": "en", "日文": "ja"}
PRIVACY_NOTE = (
    "截图与 OCR 识别完全在本机完成，不上传任何图片。\n"
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
        self._pal = palette_for_settings()
        self._win: tk.Toplevel | None = None
        self._capturing = False
        self._close_after_id = None

    # ---------------------------------------------------------------- API

    def show(self):
        if self._win is not None and self._win.winfo_exists():
            self._win.deiconify()
            self._win.focus_force()
            return
        self._pal = palette_for_settings()  # 每次打开跟随系统主题
        self._build()
        self._win.protocol("WM_DELETE_WINDOW", self._close)

    def is_shown(self) -> bool:
        return self._win is not None and self._win.winfo_exists()

    # -------------------------------------------------------------- build

    def _build(self):
        pal = self._pal
        tr = self._config.translation
        self._win = tk.Toplevel(self._root)
        self._win.title("ScreenLens 设置")
        self._win.configure(bg=pal["bg"])
        self._win.resizable(False, False)
        self._win.attributes("-topmost", True)
        self._topmost_after_id = self._win.after(200, self._unset_topmost)

        # ---- 标题 ----
        tk.Label(self._win, text="设置", bg=pal["bg"], fg=pal["fg"],
                 font=("Microsoft YaHei UI", 13, "bold")
                 ).pack(anchor="w", padx=PAD_LG, pady=(PAD_LG, 2))
        tk.Label(self._win, text="修改后点击「保存并生效」立即应用",
                 bg=pal["bg"], fg=pal["fg_dim"], font=F_SMALL
                 ).pack(anchor="w", padx=PAD_LG, pady=(0, GAP))

        # ---- 卡片：快捷键 ----
        hk_card, hk_inner = card(self._win, pal, "快捷键")
        hk_card.pack(fill="x", padx=PAD_LG, pady=(GAP, 0))
        row = tk.Frame(hk_inner, bg=pal["bg_card"])
        row.pack(fill="x", padx=PAD, pady=PAD)
        self._hotkey_var = tk.StringVar(value=self._config.hotkey)
        self._hotkey_entry = tk.Entry(
            row, textvariable=self._hotkey_var, state="readonly",
            width=24, bg=pal["bg_input"], fg=pal["fg"], relief="flat",
            font=("Consolas", 11), readonlybackground=pal["bg_input"],
            highlightthickness=1,
            highlightbackground=pal["border"])
        self._hotkey_entry.pack(side="left", ipady=4)
        self._btn_capture = FlatButton(
            row, "录入新快捷键", self._start_capture, pal)
        self._btn_capture.pack(side="left", padx=(GAP, 0))
        self._hotkey_hint = tk.Label(hk_inner, bg=pal["bg_card"],
                                     fg=pal["fg_dim"], font=F_SMALL,
                                     text="点击「录入新快捷键」后按下组合键，"
                                          "Esc 取消录入")
        self._hotkey_hint.pack(anchor="w", padx=PAD, pady=(0, PAD))

        # ---- 卡片：翻译服务 ----
        tr_card, tr_inner = card(self._win, pal, "翻译服务")
        tr_card.pack(fill="x", padx=PAD_LG, pady=(GAP, 0))
        self._provider_var = tk.StringVar(
            value=tr.get("provider", "google_free"))
        for value, label in PROVIDER_LABELS:
            tk.Radiobutton(
                tr_inner, text=label, variable=self._provider_var,
                value=value, bg=pal["bg_card"], fg=pal["fg"],
                selectcolor=pal["bg_input"], activebackground=pal["bg_card"],
                activeforeground=pal["fg"], font=F_BODY,
                command=self._on_provider_change
            ).pack(anchor="w", padx=PAD, pady=(GAP, 0))

        # OpenAI 参数（仅选择 OpenAI 时显示）
        self._oa_frame = tk.Frame(tr_inner, bg=pal["bg_card"])
        self._oa_frame.pack(fill="x", padx=PAD + 8, pady=(GAP, 4))
        oa = tr.get("openai", {})
        self._url_var = tk.StringVar(value=oa.get("base_url", ""))
        self._key_var = tk.StringVar(value=oa.get("api_key", ""))
        self._model_var = tk.StringVar(value=oa.get("model", ""))
        self._show_key = tk.BooleanVar(value=False)

        def add_field(parent, label, var, **kw):
            r = tk.Frame(parent, bg=pal["bg_card"])
            r.pack(fill="x", pady=3)
            tk.Label(r, text=label, bg=pal["bg_card"], fg=pal["fg_dim"],
                     width=10, anchor="w", font=F_SMALL
                     ).pack(side="left")
            e = tk.Entry(r, textvariable=var, bg=pal["bg_input"],
                         fg=pal["fg"], relief="flat", width=34,
                         font=F_BODY, highlightthickness=1,
                         highlightbackground=pal["border"], **kw)
            e.pack(side="left", ipady=3)
            return e

        self._url_entry = add_field(self._oa_frame, "Base URL",
                                    self._url_var)
        self._key_entry = add_field(self._oa_frame, "API Key",
                                    self._key_var, show="*")
        self._show_key_btn = FlatButton(
            self._oa_frame, "显示 API Key", self._toggle_key_visibility,
            pal, font=F_SMALL, padx=10, pady=2)
        self._show_key_btn.pack(anchor="w", pady=(2, 0))
        self._model_entry = add_field(self._oa_frame, "模型",
                                      self._model_var)
        self._on_provider_change()

        # 目标语言
        lang_row = tk.Frame(tr_inner, bg=pal["bg_card"])
        lang_row.pack(fill="x", padx=PAD, pady=(GAP, PAD))
        tk.Label(lang_row, text="默认目标语言", bg=pal["bg_card"],
                 fg=pal["fg_dim"], font=F_SMALL).pack(side="left")
        self._lang_var = tk.StringVar(
            value={"zh": "中文", "en": "英文", "ja": "日文"}.get(
                tr.get("target_language", "zh"), "中文"))
        ttk.Combobox(lang_row, textvariable=self._lang_var,
                     values=LANG_ITEMS, state="readonly", width=8,
                     font=F_BODY).pack(side="left", padx=(GAP, 0))

        # ---- 卡片：隐私 ----
        priv_card, priv_inner = card(self._win, pal, "隐私")
        priv_card.pack(fill="x", padx=PAD_LG, pady=(GAP, 0))
        tk.Label(priv_inner, text=PRIVACY_NOTE, bg=pal["bg_card"],
                 fg=pal["fg_dim"], justify="left", wraplength=430,
                 font=F_SMALL).pack(anchor="w", padx=PAD, pady=PAD)

        # ---- 底部操作行 + 内联反馈 ----
        footer = tk.Frame(self._win, bg=pal["bg"])
        footer.pack(fill="x", padx=PAD_LG, pady=(GAP, 4))
        self._status = StatusLabel(footer, pal, bg=pal["bg"])
        self._status.pack(side="left", fill="x", expand=True)
        btns = tk.Frame(footer, bg=pal["bg"])
        btns.pack(side="right")

        FlatButton(btns, "恢复默认值", self._on_reset_defaults,
                   pal).pack(side="left")
        FlatButton(btns, "取消", self._close, pal).pack(
            side="left", padx=(GAP, 0))
        FlatButton(btns, "保存并生效", self._on_save, pal,
                   kind="primary").pack(side="left", padx=(GAP, 0))
        # 底部留白
        tk.Frame(self._win, bg=pal["bg"], height=PAD_LG).pack()

    # ------------------------------------------------------- hotkey capture

    def _start_capture(self):
        self._capturing = True
        self._btn_capture.configure(text="请按下组合键（Esc 取消）…")
        self._win.bind("<KeyPress>", self._on_capture_key)
        self._win.focus_force()

    def _on_capture_key(self, event):
        hotkey, error = hotkey_from_event(event)
        if hotkey is None and error is None:
            self._stop_capture()          # Esc 取消
            return
        if hotkey is None:
            self._hotkey_hint.configure(
                text=f"{error}（Esc 取消录入）", fg=self._pal["error"])
            return
        self._hotkey_var.set(hotkey)
        self._hotkey_hint.configure(
            text=f"已录入：{hotkey}（保存后生效）",
            fg=self._pal["fg_dim"])
        self._stop_capture()

    def _stop_capture(self):
        self._capturing = False
        self._win.unbind("<KeyPress>")
        self._btn_capture.configure(text="录入新快捷键")

    # ------------------------------------------------------------- actions

    def _unset_topmost(self):
        self._topmost_after_id = None
        if self._win is not None and self._win.winfo_exists():
            try:
                self._win.attributes("-topmost", False)
            except tk.TclError:
                pass

    def _on_provider_change(self):
        """只显示所选 Provider 的相关字段。"""
        if self._provider_var.get() == "openai":
            self._oa_frame.pack(fill="x", padx=PAD + 8, pady=(GAP, 4))
        else:
            self._oa_frame.pack_forget()

    def _toggle_key_visibility(self):
        shown = not self._show_key.get()
        self._show_key.set(shown)
        self._key_entry.configure(show="" if shown else "*")
        self._show_key_btn.configure(
            text="隐藏 API Key" if shown else "显示 API Key")

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
            self._status.set_status(
                "以下问题需要修正：" + "；".join(errors), "error")
            return

        # 2) 交给应用层应用（写配置 + 注册热键 + 刷新 Provider）
        #    失败时应用层负责回滚，并返回错误说明
        apply_errors = self._apply(candidate["hotkey"],
                                   candidate["translation"])
        if apply_errors:
            self._status.set_status(
                "部分设置未生效：" + "；".join(apply_errors)
                + "。快捷键仍为当前可用的值。", "error")
            # 显示当前实际生效的快捷键
            self._hotkey_var.set(self._config.hotkey)
            return

        self._status.set_status("设置已保存并立即生效。", "success")
        # 短暂停留展示成功反馈后关闭
        if self._win is not None:
            self._close_after_id = self._win.after(900, self._close)

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
        self._status.set_status("已恢复默认值（尚未保存）。", "info")

    def _close(self):
        if self._win is None:
            return
        for after_id in (getattr(self, "_topmost_after_id", None),
                         self._close_after_id):
            if after_id is not None:
                try:
                    self._win.after_cancel(after_id)
                except Exception:
                    pass
        self._topmost_after_id = None
        self._close_after_id = None
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
