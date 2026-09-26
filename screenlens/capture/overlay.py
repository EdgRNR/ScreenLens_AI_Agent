# -*- coding: utf-8 -*-
"""截图遮罩层：全屏冻结画面 + 暗色遮罩 + 矩形/自由形状选区。

交互（第三阶段 UI 改版）：
- 显式模式工具条：矩形 / 自由圈选 两个可点击模式按钮（默认矩形）
- 保留快捷手势：Ctrl+左键 / 右键拖动 临时使用自由圈选
- 拖动中实时显示选区边框与尺寸（宽 × 高）
- 松开鼠标后选区进入待确认状态，工具条切换为
  「识别并取词 (Enter)」/「取消 (Esc)」，Enter 或点按钮完成
- 选区太小：保留遮罩并提示重试
- 工具条避让当前选区与屏幕边缘
"""
import time
import tkinter as tk

from PIL import Image, ImageEnhance, ImageTk

from screenlens.capture import screen as screen_util
from screenlens.capture import region as region_util
from screenlens.ui.theme import DARK as PAL

DARK_FACTOR = 0.55          # 遮罩暗度（越大越亮）
PREVIEW_INTERVAL = 0.04     # 自由圈选预览刷新间隔（秒）

MIN_SELECTION = 12          # 选区最小边长（像素），小于视为误点击
TOOLBAR_MARGIN = 12         # 工具条与选区/屏幕边缘的安全距离
TOOLBAR_W = 264             # 工具条预估宽度（place 布局用）
TOOLBAR_H = 44              # 工具条预估高度


class _ToolbarButton(tk.Label):
    """遮罩上的工具条按钮（深色高对比，避免遮罩上看不清）。"""

    def __init__(self, parent, text, command, selected=False):
        self._command = command
        self._selected = selected
        super().__init__(parent, text=text, cursor="hand2",
                         font=("Microsoft YaHei UI", 10),
                         padx=12, pady=5)
        self._apply()
        self.bind("<ButtonRelease-1>", self._fire)

    def _apply(self):
        if self._selected:
            self.configure(bg="#2fa8e0", fg="#0b1418")
        else:
            self.configure(bg="#333844", fg="#e9ebf0")

    def set_selected(self, selected: bool):
        self._selected = selected
        self._apply()

    def _fire(self, _e):
        if self._command:
            self._command()


class CaptureOverlay:
    """全屏截图选区遮罩。

    用法:
        overlay = CaptureOverlay(root, on_captured, on_cancel)
        overlay.start()   # 异步：先冻结屏幕，再显示遮罩
    on_captured(crop: PIL.Image, screen_bbox: tuple)  # 选区图像 + 屏幕坐标
    """

    def __init__(self, root: tk.Tk, on_captured, on_cancel):
        self._root = root
        self._on_captured = on_captured
        self._on_cancel = on_cancel

        self._win: tk.Toplevel | None = None
        self._canvas: tk.Canvas | None = None
        self._toolbar: tk.Frame | None = None
        self._screen_img: Image.Image | None = None
        self._dimmed_photo: ImageTk.PhotoImage | None = None
        self._sharp_photo: ImageTk.PhotoImage | None = None
        self._vleft = self._vtop = 0
        self._vw = self._vh = 0

        self._mode = "rect"        # 工具条当前选择的模式: "rect" | "free"
        self._start = None         # 拖动起点 (canvas 坐标)
        self._points: list[tuple[int, int]] = []
        self._last_preview = 0.0
        self._finished = False

        # 待确认选区（松开鼠标后、Enter 确认前）
        self._pending = None       # {"mode", "start", "points", "bbox"}

    # ------------------------------------------------------------------ API

    def start(self):
        """冻结屏幕并显示遮罩（延迟一帧，确保其他窗口已隐藏）。"""
        self._root.after(150, self._show)

    def _show(self):
        try:
            self._screen_img, bbox = screen_util.grab_screen()
        except Exception:
            self._on_cancel("无法截取屏幕画面")
            return

        self._vleft, self._vtop = bbox["left"], bbox["top"]
        w, h = bbox["width"], bbox["height"]
        self._vw, self._vh = w, h
        # 主显示器（canvas 坐标系下）——浮动工具条的参照区域
        try:
            pm = screen_util.primary_monitor_bbox()
            self._pm_left = pm["left"] - self._vleft
            self._pm_top = pm["top"] - self._vtop
            self._pm_w, self._pm_h = pm["width"], pm["height"]
        except Exception:
            self._pm_left = self._pm_top = 0
            self._pm_w, self._pm_h = w, h

        dimmed = ImageEnhance.Brightness(self._screen_img).enhance(
            DARK_FACTOR)

        self._win = tk.Toplevel(self._root)
        self._win.overrideredirect(True)
        self._win.attributes("-topmost", True)
        self._win.geometry(f"{w}x{h}+{self._vleft}+{self._vtop}")

        self._canvas = tk.Canvas(self._win, width=w, height=h,
                                 highlightthickness=0, cursor="crosshair")
        self._canvas.pack(fill="both", expand=True)
        self._dimmed_photo = ImageTk.PhotoImage(dimmed, master=self._win)
        self._canvas.create_image(0, 0, image=self._dimmed_photo,
                                  anchor="nw")

        self._build_toolbar()
        self._bind_events()
        self._win.focus_force()

    # ------------------------------------------------------------- toolbar

    def _build_toolbar(self):
        """轻量浮动工具条：模式选择 / 确认动作。"""
        self._toolbar = tk.Frame(self._canvas, bg="#242730",
                                 highlightthickness=1,
                                 highlightbackground="#3a3f4c")
        self._btn_mode_rect = _ToolbarButton(
            self._toolbar, "▭ 矩形",
            lambda: self._select_mode("rect"),
            selected=True)
        self._btn_mode_rect.pack(side="left", padx=(8, 2), pady=6)
        self._btn_mode_free = _ToolbarButton(
            self._toolbar, "✎ 自由圈选",
            lambda: self._select_mode("free"))
        self._btn_mode_free.pack(side="left", padx=2, pady=6)
        self._tb_hint = tk.Label(self._toolbar, text="拖动选择 · Enter 确认 · "
                               "Esc 取消", bg="#242730", fg="#9aa0ac",
                               font=("Microsoft YaHei UI", 9))
        self._tb_hint.pack(side="left", padx=10)
        # 工具条后构建（覆盖 canvas）
        self._toolbar.place(x=0, y=0)
        self._layout_toolbar_idle()

    def _select_mode(self, mode: str):
        """工具条显式切换模式；有待确认选区时忽略。"""
        if self._pending or self._finished:
            return
        self._mode = mode
        self._btn_mode_rect.set_selected(mode == "rect")
        self._btn_mode_free.set_selected(mode == "free")

    def _toolbar_show_confirm(self):
        """切换为待确认状态：识别 / 取消。"""
        for child in self._toolbar.winfo_children():
            child.destroy()
        self._btn_ok = _ToolbarButton(
            self._toolbar, "✓ 识别并取词 (Enter)", self._confirm)
        self._btn_ok.configure(bg="#2fa8e0", fg="#0b1418",
                               font=("Microsoft YaHei UI", 10, "bold"))
        self._btn_ok.pack(side="left", padx=(8, 2), pady=6)
        self._btn_cancel = _ToolbarButton(
            self._toolbar, "✕ 取消 (Esc)", self._cancel)
        self._btn_cancel.pack(side="left", padx=2, pady=6)

    def _toolbar_show_mode(self):
        """切回模式选择状态。"""
        for child in self._toolbar.winfo_children():
            child.destroy()
        self._btn_mode_rect = _ToolbarButton(
            self._toolbar, "▭ 矩形",
            lambda: self._select_mode("rect"),
            selected=self._mode == "rect")
        self._btn_mode_rect.pack(side="left", padx=(8, 2), pady=6)
        self._btn_mode_free = _ToolbarButton(
            self._toolbar, "✎ 自由圈选",
            lambda: self._select_mode("free"),
            selected=self._mode == "free")
        self._btn_mode_free.pack(side="left", padx=2, pady=6)
        self._tb_hint = tk.Label(self._toolbar,
                                 text="拖动选择 · Enter 确认 · Esc 取消",
                                 bg="#242730", fg="#9aa0ac",
                                 font=("Microsoft YaHei UI", 9))
        self._tb_hint.pack(side="left", padx=10)

    # -------------------------------------------------------- toolbar 位置

    def _measure_toolbar(self):
        """先放到原点让 Tk 完成一次几何计算，取得工具条实际尺寸。

        Tk 的 winfo_reqwidth 对尚未映射的控件不可靠，因此这里先 place
        再读数；测量与最终定位发生在同一帧内，用户看不到跳动。
        """
        try:
            self._toolbar.place(x=0, y=0)
            self._toolbar.update_idletasks()
            self._tb_w = max(self._toolbar.winfo_width(), 1)
            self._tb_h = max(self._toolbar.winfo_height(), 1)
        except tk.TclError:
            self._tb_w, self._tb_h = TOOLBAR_W, TOOLBAR_H
        if self._tb_w <= 1:
            self._tb_w = TOOLBAR_W
        if self._tb_h <= 1:
            self._tb_h = TOOLBAR_H

    def _tb_size(self) -> tuple[int, int]:
        """工具条实际尺寸（内容随状态变化，用实测值而非固定常量）。"""
        if not getattr(self, "_tb_w", None):
            self._measure_toolbar()
        return self._tb_w, self._tb_h

    def _layout_toolbar_idle(self):
        """空闲：主显示器底部居中（多显示器下不会落进屏幕缝隙）。"""
        self._measure_toolbar()
        w, h = self._tb_size()
        x = self._pm_left + max(0, (self._pm_w - w) // 2)
        y = max(self._pm_top,
                self._pm_top + self._pm_h - h - TOOLBAR_MARGIN * 3)
        self._toolbar.place(x=x, y=y)

    def _toolbar_area(self, bbox) -> tuple[int, int, int, int]:
        """工具条允许出现的区域：选区所在显示器（主屏优先）。"""
        cx, cy = (bbox[0] + bbox[2]) // 2, (bbox[1] + bbox[3]) // 2
        if (self._pm_left <= cx <= self._pm_left + self._pm_w
                and self._pm_top <= cy <= self._pm_top + self._pm_h):
            return (self._pm_left, self._pm_top,
                    self._pm_left + self._pm_w, self._pm_top + self._pm_h)
        return (0, 0, self._vw, self._vh)

    def _layout_toolbar_near(self, x0, y0, x1, y1):
        """避让选区（bbox 之外）与屏幕边缘，放在选区下方/上方/角落。"""
        margin = TOOLBAR_MARGIN
        self._measure_toolbar()
        tw, th = self._tb_size()
        left, top, right, bottom = self._toolbar_area((x0, y0, x1, y1))
        # 首选：选区正下方
        x = x0
        y = y1 + margin
        if y + th > bottom - margin:
            # 其次：选区上方
            y = y0 - margin - th
        if y < top + margin:
            # 最后：该显示器底部（不与选区重叠优先）
            y = bottom - th - margin
            if y0 < y + th < y1:
                y = top + margin
        x = max(left + margin, min(x, right - tw - margin))
        y = max(top, min(y, bottom - th))
        self._toolbar.place(x=x, y=y)

    def _toolbar_overlaps(self, bbox) -> bool:
        """工具条当前显示区域是否与 bbox 重叠。"""
        if self._toolbar is None:
            return False
        try:
            x = self._toolbar.winfo_x()
            y = self._toolbar.winfo_y()
            tw, th = self._tb_size()
            w = self._toolbar.winfo_width() or tw
            h = self._toolbar.winfo_height() or th
        except tk.TclError:
            return False
        return not (bbox[2] < x or bbox[0] > x + w
                    or bbox[3] < y or bbox[1] > y + h)

    def _avoid_toolbar(self, bbox):
        """拖动中：工具条若与选区重叠则实时移开。"""
        if self._toolbar_overlaps(bbox):
            self._layout_toolbar_near(*bbox)

    # --------------------------------------------------------------- events

    def _bind_events(self):
        c = self._canvas
        c.bind("<ButtonPress-1>", lambda e: self._press(e, "rect"))
        c.bind("<Control-ButtonPress-1>", lambda e: self._press(e, "free"))
        c.bind("<ButtonPress-3>", lambda e: self._press(e, "free"))
        c.bind("<B1-Motion>", self._motion)
        c.bind("<Control-B1-Motion>", self._motion)
        c.bind("<B3-Motion>", self._motion)
        c.bind("<ButtonRelease-1>", self._release)
        c.bind("<Control-ButtonRelease-1>", self._release)
        c.bind("<ButtonRelease-3>", self._release)
        self._win.bind("<Escape>", self._cancel)
        self._win.bind("<Return>", self._confirm)
        self._canvas.bind("<Escape>", self._cancel)
        self._canvas.bind("<Return>", self._confirm)
        self._win.protocol("WM_DELETE_WINDOW", self._cancel)

    def _press(self, event, mode):
        if self._finished:
            return
        if self._pending:
            # 点击选区外：放弃待确认选区，重新开始
            self._discard_pending()
        # Ctrl/右键是临时模式；普通拖动用工具条选择的模式
        self._drag_mode = mode if mode == "free" else self._mode
        self._start = (event.x, event.y)
        self._points = [(event.x, event.y)]
        self._clear_preview()

    def _motion(self, event):
        if self._start is None or self._finished:
            return
        mode = getattr(self, "_drag_mode", self._mode)
        if mode == "rect":
            self._update_rect_preview(event.x, event.y)
        else:
            self._points.append((event.x, event.y))
            now = time.monotonic()
            if now - self._last_preview >= PREVIEW_INTERVAL:
                self._last_preview = now
                self._update_free_preview()

    def _release(self, event):
        if self._start is None or self._finished:
            return
        mode = getattr(self, "_drag_mode", self._mode)
        start = self._start
        points = list(self._points)
        if mode == "free":
            points.append((event.x, event.y))
        self._start = None
        self._points = []

        if mode == "rect":
            bbox = region_util.rect_bbox(start, (event.x, event.y))
            ok = self._selection_big_enough(bbox)
        else:
            bbox = region_util.path_bbox(points)
            ok = (self._selection_big_enough(bbox)
                  and len(points) >= 8)

        if not ok:
            # 选区太小（几乎是一次点击）→ 提示后允许重新选择
            self._clear_preview()
            self._show_tiny_hint()
            return

        self._pending = {"mode": mode, "start": start,
                         "points": points, "bbox": bbox}
        self._toolbar_show_confirm()
        self._layout_toolbar_near(*bbox)

    def _selection_big_enough(self, bbox) -> bool:
        if not region_util.bbox_valid(bbox):
            return False
        return (bbox[2] - bbox[0]) >= MIN_SELECTION and \
               (bbox[3] - bbox[1]) >= MIN_SELECTION

    def _show_tiny_hint(self):
        """小提示：选区太小，保留遮罩继续选择。"""
        self._canvas.delete("tinyhint")
        self._canvas.create_text(
            self._vw // 2, self._vh - 90,
            text="选区太小，请重新拖动选择（矩形至少 12×12）",
            fill="#f3c969", font=("Microsoft YaHei UI", 10),
            tags="tinyhint")
        self._win.after(1600, lambda: self._canvas.delete("tinyhint")
                        if self._canvas else None)

    def _discard_pending(self):
        self._pending = None
        self._clear_preview()
        self._toolbar_show_mode()
        self._layout_toolbar_idle()

    # ------------------------------------------------------------- confirm

    def _confirm(self, _event=None):
        """Enter / 工具条按钮：完成待确认选区。"""
        if self._finished or not self._pending:
            return
        pending = self._pending
        self._pending = None
        self._finished = True
        mode, bbox = pending["mode"], pending["bbox"]
        if mode == "rect":
            crop = region_util.crop_rect(self._screen_img, bbox)
        else:
            crop = region_util.crop_freeform(self._screen_img,
                                             pending["points"])
        screen_bbox = (bbox[0] + self._vleft, bbox[1] + self._vtop,
                       bbox[2] + self._vleft, bbox[3] + self._vtop)
        self._close()
        self._on_captured(crop, screen_bbox)

    def _cancel(self, _event=None):
        if self._finished:
            return
        self._finished = True
        self._close()
        self._on_cancel("cancelled")

    # -------------------------------------------------------------- preview

    def _clear_preview(self):
        self._canvas.delete("sel")
        self._canvas.delete("sizesel")
        self._sharp_photo = None

    def _draw_size_label(self, bbox):
        """选区旁显示宽 × 高。"""
        x0, y0, x1, y1 = bbox
        w, h = x1 - x0, y1 - y0
        label = f"{w} × {h}"
        # 放在选区右上角外侧；贴边时放内侧
        tx, ty = x0, y0 - 10
        anchor = "sw"
        if ty - 14 < 0:
            ty = y0 + 2
            anchor = "nw"
        if tx < 4:
            tx = 4
        self._canvas.create_text(
            tx, ty, text=label, fill="#2fa8e0", anchor=anchor,
            font=("Consolas", 10, "bold"), tags=("sel", "sizesel"))

    def _update_rect_preview(self, x, y):
        bbox = region_util.rect_bbox(self._start, (x, y))
        self._canvas.delete("sel")
        x0, y0, x1, y1 = bbox
        # 选区内贴上清晰的原始画面
        if x1 > x0 and y1 > y0:
            crop = self._screen_img.crop((x0, y0, x1, y1))
            self._sharp_photo = ImageTk.PhotoImage(crop, master=self._win)
            self._canvas.create_image(x0, y0, image=self._sharp_photo,
                                      anchor="nw", tags="sel")
        self._canvas.create_rectangle(
            x0, y0, x1, y1, outline="#2fa8e0", width=2, tags="sel")
        self._draw_size_label(bbox)
        self._avoid_toolbar(bbox)

    def _update_free_preview(self):
        if len(self._points) < 3:
            return
        try:
            crop = region_util.make_alpha_preview(
                self._screen_img, self._points)
        except Exception:
            return
        x0, y0, x1, y1 = region_util.path_bbox(self._points)
        self._canvas.delete("sel")
        self._sharp_photo = ImageTk.PhotoImage(crop, master=self._win)
        self._canvas.create_image(x0, y0, image=self._sharp_photo,
                                  anchor="nw", tags="sel")
        flat = [c for p in self._points for c in p]
        self._canvas.create_line(
            *flat, fill="#2fa8e0", width=2, tags="sel", smooth=True)
        self._draw_size_label((x0, y0, x1, y1))
        self._avoid_toolbar((x0, y0, x1, y1))

    # ---------------------------------------------------------------- close

    def _close(self):
        self._canvas = None
        self._toolbar = None
        self._sharp_photo = None
        self._dimmed_photo = None
        self._screen_img = None
        self._pending = None
        if self._win is not None:
            try:
                self._win.destroy()
            except tk.TclError:
                pass
            self._win = None
        # 主线程立即回收控件循环引用，防止工作线程 GC 触发
        # 跨线程 Tcl 调用（Tcl_AsyncDelete 崩溃）
        import gc

        gc.collect()
