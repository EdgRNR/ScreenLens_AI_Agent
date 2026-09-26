# -*- coding: utf-8 -*-
"""第三阶段 UI 改版验收：生成三个关键界面的实际截图。

输出到 docs/screenshots/：
1. overlay.png   截图遮罩（工具条 + 矩形选区 + 尺寸反馈）
2. result.png   结果面板（识别文字 + 翻译结果分层）
3. settings.png 设置窗口（分组卡片）

演示不依赖网络：翻译结果直接注入。
"""
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import tkinter as tk

from PIL import ImageGrab

OUT = os.path.join(os.path.dirname(__file__), "..", "docs", "screenshots")


def grab(bbox):
    """多显示器截图（all_screens 必要，否则副屏区域为黑）。"""
    try:
        return ImageGrab.grab(bbox, all_screens=True)
    except TypeError:
        return ImageGrab.grab(bbox)


def pump(root, ms=50):
    end = time.time() + ms / 1000
    while time.time() < end:
        root.update()
        time.sleep(0.01)


def snap(root, widget, name, pad=6):
    root.update_idletasks()
    x = widget.winfo_rootx() - pad
    y = widget.winfo_rooty() - pad
    w = widget.winfo_width() + pad * 2
    h = widget.winfo_height() + pad * 2
    img = grab((x, y, x + w, y + h))
    img.save(os.path.join(OUT, name))
    print("saved", name, img.size)
    return img


def demo_overlay(root):
    from screenlens.capture.overlay import CaptureOverlay

    state = {"captured": None}

    def on_captured(crop, bbox):
        state["captured"] = (crop, bbox)

    def crop_toolbar(full, name, pad=14):
        """从整屏遮罩截图按 canvas 坐标裁剪工具条特写。"""
        root.update_idletasks()
        tb = overlay._toolbar
        tx, ty = tb.winfo_x(), tb.winfo_y()
        tw = tb.winfo_width() or 264
        th = tb.winfo_height() or 44
        full.crop((max(0, tx - pad), max(0, ty - pad),
                   tx + tw + pad, ty + th + pad)).save(
            os.path.join(OUT, name))
        print("saved", name, (tx, ty, tw, th))

    overlay = CaptureOverlay(root, on_captured, lambda r: None)
    overlay.start()
    pump(root, 600)
    # 空闲态：矩形 / 自由圈选 入口
    crop_toolbar(snap(root, overlay._win, "overlay.png", pad=0),
                 "overlay_modes.png")
    c = overlay._canvas
    # 矩形拖动（会显示边框、尺寸标签，工具条避让）
    c.event_generate("<ButtonPress-1>", x=260, y=160)
    pump(root, 60)
    for i in range(1, 9):
        c.event_generate("<B1-Motion>", x=260 + i * 44, y=160 + i * 20)
        pump(root, 30)
    c.event_generate("<ButtonRelease-1>", x=612, y=320)
    pump(root, 100)
    # 全屏遮罩 + 选区/工具条局部特写
    full = snap(root, overlay._win, "overlay.png", pad=0)
    tx = overlay._toolbar.winfo_x()
    ty = overlay._toolbar.winfo_y()
    th = overlay._toolbar.winfo_height()
    box = (200, 100, 760, max(ty + th, 340) + 30)
    full.crop(box).save(os.path.join(OUT, "overlay_detail.png"))
    print("saved overlay_detail.png (crop)")
    overlay._cancel()
    pump(root, 100)


def demo_result(root):
    from tests.test_ocr import make_test_image
    from PIL import Image
    from screenlens.ui.result_window import ResultWindow

    class CfgStub:
        translation = {"provider": "google_free", "target_language": "zh"}

    from screenlens.ocr.engine import OcrEngine

    engine = OcrEngine()
    engine.warmup_async()
    engine._ensure_engine()
    rw = ResultWindow(root, engine, CfgStub(), on_recapture=lambda: None)
    with Image.open(make_test_image()) as img:
        crop = img.copy()
    rw.show_ocr(crop, (120, 120, 820, 520))
    # 等 OCR 完成
    deadline = time.time() + 30
    while time.time() < deadline:
        pump(root, 100)
        if rw._has_text:
            break
    # 注入翻译结果（避免网络依赖）
    rw._trans_text.insert("1.0",
                          "Hello ScreenLens\nThe weather is nice today, "
                          "suitable for a walk.\nThis is Japanese text.")
    rw._trans_frame.pack(fill="both", expand=True, before=rw._toolbar)
    x, y = rw._win.winfo_x(), rw._win.winfo_y()
    rw._win.geometry(f"480x500+{x}+{y}")
    pump(root, 150)
    snap(root, rw._win, "result.png")
    rw.close()


def demo_settings(root):
    import tempfile
    from screenlens.config import Config
    from screenlens.ui.settings_window import SettingsWindow

    fd, path = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    os.remove(path)
    config = Config(path)
    config.load()
    win = SettingsWindow(root, config, lambda h, t: [])
    win.show()
    pump(root, 300)
    snap(root, win._win, "settings.png")
    win._close()
    if os.path.exists(path):
        os.remove(path)


def main():
    os.makedirs(OUT, exist_ok=True)
    root = tk.Tk()
    root.withdraw()
    try:
        demo_overlay(root)
        demo_result(root)
        demo_settings(root)
    finally:
        try:
            root.destroy()
        except tk.TclError:
            pass
    print("done")


if __name__ == "__main__":
    main()
