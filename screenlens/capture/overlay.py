# -*- coding: utf-8 -*-
"""截图遮罩层：全屏冻结画面 + 暗色遮罩 + 矩形/自由形状选区。

交互设计（零学习成本）：
- 左键拖动            → 矩形选区（最熟悉的交互）
- Ctrl+左键拖动 / 右键拖动 → 自由圈选（圆形、椭圆、任意曲线）
- Esc                 → 取消截图
- 选区内画面保持清晰，选区外为半透明暗色
"""
import time
import tkinter as tk

from PIL import Image, ImageEnhance, ImageTk

from screenlens.capture import screen as screen_util
from screenlens.capture import region as region_util

DARK_FACTOR = 0.55          # 遮罩暗度（越大越亮）
PREVIEW_INTERVAL = 0.04     # 自由圈选预览刷新间隔（秒）
HINT_TEXT = "左键拖动：矩形选区    Ctrl/右键拖动：自由圈选    Esc：取消"


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
        self._screen_img: Image.Image | None = None
        self._dimmed_photo: ImageTk.PhotoImage | None = None
        self._sharp_photo: ImageTk.PhotoImage | None = None
        self._vleft = self._vtop = 0

        self._mode = None            # "rect" | "free"
        self._start = None           # 拖动起点 (canvas 坐标)
        self._points: list[tuple[int, int]] = []
        self._last_preview = 0.0
        self._finished = False

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

        dimmed = ImageEnhance.Brightness(self._screen_img).enhance(DARK_FACTOR)

        self._win = tk.Toplevel(self._root)
        self._win.overrideredirect(True)
        self._win.attributes("-topmost", True)
        self._win.geometry(f"{w}x{h}+{self._vleft}+{self._vtop}")

        self._canvas = tk.Canvas(self._win, width=w, height=h,
                                 highlightthickness=0, cursor="crosshair")
        self._canvas.pack(fill="both", expand=True)
        self._dimmed_photo = ImageTk.PhotoImage(dimmed, master=self._win)
        self._canvas.create_image(0, 0, image=self._dimmed_photo, anchor="nw")

        self._canvas.create_text(
            w // 2, h - 28, text=HINT_TEXT, fill="#ffffff",
            font=("Microsoft YaHei UI", 11),
            tags="hint")

        self._bind_events()
        self._win.focus_force()

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
        c.bind("<Escape>", self._cancel)
        self._win.protocol("WM_DELETE_WINDOW", self._cancel)

    def _press(self, event, mode):
        self._mode = mode
        self._start = (event.x, event.y)
        self._points = [(event.x, event.y)]
        self._clear_preview()

    def _motion(self, event):
        if self._mode is None:
            return
        if self._mode == "rect":
            self._update_rect_preview(event.x, event.y)
        else:
            self._points.append((event.x, event.y))
            now = time.monotonic()
            if now - self._last_preview >= PREVIEW_INTERVAL:
                self._last_preview = now
                self._update_free_preview()

    def _release(self, event):
        if self._mode is None or self._finished:
            return
        self._finished = True
        if self._mode == "free":
            self._points.append((event.x, event.y))
        mode, points, start = self._mode, list(self._points), self._start
        self._mode = None
        self._start = None
        self._points = []

        if mode == "rect":
            bbox = region_util.rect_bbox(start, (event.x, event.y))
            ok = region_util.bbox_valid(bbox)
        else:
            bbox = region_util.path_bbox(points)
            ok = region_util.bbox_valid(bbox) and len(points) >= 8

        if not ok:
            # 选区太小（几乎是一次点击）→ 重置，允许重新选择
            self._finished = False
            self._clear_preview()
            return

        if mode == "rect":
            crop = region_util.crop_rect(self._screen_img, bbox)
        else:
            crop = region_util.crop_freeform(self._screen_img, points)
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
        self._sharp_photo = None

    def _update_rect_preview(self, x, y):
        bbox = region_util.rect_bbox(self._start, (x, y))
        self._canvas.delete("sel")
        x0, y0, x1, y1 = bbox
        # 选区内贴上清晰的原始画面
        crop = self._screen_img.crop((x0, y0, x1, y1))
        if crop.width > 0 and crop.height > 0:
            self._sharp_photo = ImageTk.PhotoImage(crop, master=self._win)
            self._canvas.create_image(x0, y0, image=self._sharp_photo,
                                      anchor="nw", tags="sel")
        self._canvas.create_rectangle(
            x0, y0, x1, y1, outline="#00c2ff", width=2, tags="sel")

    def _update_free_preview(self):
        if len(self._points) < 3:
            return
        try:
            crop = region_util.make_alpha_preview(
                self._screen_img, self._points)
        except Exception:
            return
        x0, y0, _, _ = region_util.path_bbox(self._points)
        self._canvas.delete("sel")
        self._sharp_photo = ImageTk.PhotoImage(crop, master=self._win)
        self._canvas.create_image(x0, y0, image=self._sharp_photo,
                                  anchor="nw", tags="sel")
        flat = [c for p in self._points for c in p]
        self._canvas.create_line(
            *flat, fill="#00c2ff", width=2, tags="sel", smooth=True)

    # ---------------------------------------------------------------- close

    def _close(self):
        self._canvas = None
        self._sharp_photo = None
        self._dimmed_photo = None
        self._screen_img = None
        if self._win is not None:
            try:
                self._win.destroy()
            except tk.TclError:
                pass
            self._win = None
