# -*- coding: utf-8 -*-
"""ScreenLens 统一 UI 主题：颜色、字体、间距与控件工厂。

设计原则（见 `docs/plans/ScreenLens_UI_交互调研与改版建议.md` 第 5 节）：
- ScreenLens 蓝为唯一主强调色；错误/成功/辅助信息使用语义色；
- 系统 UI 字体，层级区分标题/正文/辅助提示；
- 控件统一高度、间距与 悬停/按下/禁用 状态；
- 状态不能只靠颜色传达（图标 + 文字）。

技术判断结论：P0 目标在 Tkinter 内可达成，仅圆角控件不支持
（需 Canvas 自绘，收益低），故不做圆角，保持 flat + 细描边风格。
"""
import tkinter as tk

# ---------------------------------------------------------------- 调色板

# 深色（截图遮罩 / 结果面板：截图场景下对比度更好）
DARK = {
    "bg":        "#1c1e24",
    "bg_card":   "#242730",
    "bg_input":  "#2c303a",
    "border":    "#3a3f4c",
    "fg":        "#e9ebf0",
    "fg_dim":    "#9aa0ac",
    "accent":    "#2fa8e0",
    "accent_hi": "#4dbdef",
    "accent_lo": "#2589bb",
    "on_accent": "#0b1418",
    "error":     "#ff7a76",
    "success":   "#5fd39a",
    "warn":      "#f3c969",
}

# 浅色（设置页：跟随系统）
LIGHT = {
    "bg":        "#f4f5f7",
    "bg_card":   "#ffffff",
    "bg_input":  "#ffffff",
    "border":    "#d4d7dd",
    "fg":        "#1b1e24",
    "fg_dim":    "#5d6470",
    "accent":    "#0f7fc2",
    "accent_hi": "#2b94d6",
    "accent_lo": "#0c6aa3",
    "on_accent": "#ffffff",
    "error":     "#c7382f",
    "success":   "#1c7c4a",
    "warn":      "#8a6116",
}

# 字体层级
F_TITLE = ("Microsoft YaHei UI", 10, "bold")
F_BODY = ("Microsoft YaHei UI", 10)
F_SMALL = ("Microsoft YaHei UI", 9)
F_MONO = ("Consolas", 11)

# 统一间距
PAD = 12            # 卡片内边距
PAD_LG = 16         # 窗口边距
GAP = 8             # 控件间距


def system_prefers_dark() -> bool:
    """读取 Windows 系统应用主题（浅色则 False；非 Windows / 失败回退深色）。"""
    try:
        import winreg

        key = winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion"
            r"\Themes\Personalize")
        value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        winreg.CloseKey(key)
        return value == 0
    except Exception:
        return True


def palette_for_settings() -> dict:
    """设置页调色板：跟随系统主题。"""
    return DARK if system_prefers_dark() else LIGHT


# ---------------------------------------------------------------- 控件

class FlatButton(tk.Label):
    """统一按钮：primary / secondary / ghost 三种，
    支持悬停、按下、禁用状态；禁用同时改变颜色与光标。"""

    def __init__(self, parent, text, command, pal: dict,
                 kind: str = "secondary", font=None, padx=14, pady=5):
        self._pal = pal
        self._kind = kind
        self._command = command
        self._enabled = True
        self._pressing = False
        super().__init__(parent, text=text, cursor="hand2",
                         font=font or F_BODY, padx=padx, pady=pady)
        self._apply_colors()
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<ButtonPress-1>", self._on_press)
        self.bind("<ButtonRelease-1>", self._on_release)

    # ---- 颜色状态机 ----

    def _base_colors(self):
        p = self._pal
        if self._kind == "primary":
            return p["accent"], p["on_accent"], p["accent_hi"], p["accent_lo"]
        return (p["bg_input"], p["fg"], p["accent"], p["bg_input"])

    def _apply_colors(self):
        p = self._pal
        bg, fg = self._base_colors()[:2]
        if not self._enabled:
            bg, fg = p["bg_card"], p["fg_dim"]
            self.configure(bg=bg, fg=fg, cursor="arrow")
            return
        if self._pressing:
            bg, fg = self._base_colors()[3], self._base_colors()[1]
        self.configure(bg=bg, fg=fg, cursor="hand2")

    def _on_enter(self, _e):
        if self._enabled and not self._pressing:
            self.configure(bg=self._base_colors()[2])

    def _on_leave(self, _e):
        self._pressing = False
        self._apply_colors()

    def _on_press(self, _e):
        if self._enabled:
            self._pressing = True
            self._apply_colors()

    def _on_release(self, _e):
        if not self._enabled:
            return
        was_pressing = self._pressing
        self._pressing = False
        self._apply_colors()
        if was_pressing and self._command:
            self._command()

    # ---- 公开 API ----

    def set_enabled(self, enabled: bool):
        if self._enabled != enabled:
            self._enabled = enabled
            self._apply_colors()


class StatusLabel(tk.Label):
    """语义状态栏：info / busy / success / error，
    带图标前缀，不只靠颜色传达状态。"""

    _ICONS = {"info": "ℹ", "busy": "…", "success": "✓", "error": "✕"}

    def __init__(self, parent, pal: dict, **kw):
        self._pal = pal
        super().__init__(parent, text="", anchor="w", font=F_SMALL, **kw)
        self.configure(bg=kw.get("bg", pal["bg"]))
        self.set_status("", "info")

    def set_status(self, msg: str, level: str = "info"):
        """level: info / busy / success / error"""
        level = level if level in self._ICONS else "info"
        icon = self._ICONS[level]
        text = f" {icon}  {msg}" if msg else ""
        color = {"info": self._pal["fg_dim"], "busy": self._pal["fg_dim"],
                 "success": self._pal["success"],
                 "error": self._pal["error"]}[level]
        self.configure(text=text, fg=color)


def card(parent, pal: dict, title: str | None = None):
    """分组卡片：返回 (卡片外框, 内容容器)。"""
    outer = tk.Frame(parent, bg=pal["bg"], highlightthickness=1,
                     highlightbackground=pal["border"])
    inner = tk.Frame(outer, bg=pal["bg_card"])
    if title is not None:
        tk.Label(outer, text=title, bg=pal["bg"], fg=pal["fg_dim"],
                 font=F_SMALL, anchor="w").pack(fill="x", padx=PAD,
                                                pady=(0, 1))
    inner.pack(fill="both", expand=True, padx=1, pady=(0, 1) if title
               else 1)
    return outer, inner


def card_header(parent, pal: dict, title: str):
    """卡片内的小节标题行。"""
    return tk.Label(parent, text=title, bg=pal["bg_card"],
                    fg=pal["fg_dim"], font=F_SMALL, anchor="w")
