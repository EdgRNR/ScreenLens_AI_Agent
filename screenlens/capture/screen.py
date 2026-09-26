# -*- coding: utf-8 -*-
"""全屏截图工具（基于 mss，支持多显示器虚拟屏幕）。"""
import ctypes

import mss
from PIL import Image


def _grab(monitor_or_region: dict):
    """兼容 mss 新旧版本的截图辅助（mss.mss 已弃用，改为 mss.MSS）。"""
    factory = getattr(mss, "MSS", mss.mss)
    with factory() as sct:
        shot = sct.grab(monitor_or_region)
        return Image.frombytes("RGB", shot.size, shot.rgb)


def set_dpi_awareness() -> None:
    """让进程感知 DPI，保证 tkinter 坐标与屏幕物理像素一致。"""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PER_MONITOR_DPI_AWARE
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def virtual_screen_bbox() -> dict:
    """返回整个虚拟屏幕（所有显示器的包围盒）的 {left, top, width, height}。"""
    factory = getattr(mss, "MSS", mss.mss)
    with factory() as sct:
        m = sct.monitors[0]  # monitors[0] = 所有显示器的并集
        return {"left": m["left"], "top": m["top"],
                "width": m["width"], "height": m["height"]}


def grab_screen() -> tuple[Image.Image, dict]:
    """截取整个虚拟屏幕。

    返回 (PIL Image, bbox)，bbox 为该图像对应的屏幕坐标
    {left, top, width, height}。
    """
    factory = getattr(mss, "MSS", mss.mss)
    with factory() as sct:
        monitor = sct.monitors[0]
        shot = sct.grab(monitor)
        img = Image.frombytes("RGB", shot.size, shot.rgb)
        bbox = {"left": monitor["left"], "top": monitor["top"],
                "width": monitor["width"], "height": monitor["height"]}
        return img, bbox


def grab_region(x0: int, y0: int, x1: int, y1: int) -> Image.Image:
    """截取屏幕上指定矩形区域（屏幕坐标）。"""
    left, top = min(x0, x1), min(y0, y1)
    right, bottom = max(x0, x1), max(y0, y1)
    region = {"left": left, "top": top,
              "width": max(1, right - left), "height": max(1, bottom - top)}
    return _grab(region)
