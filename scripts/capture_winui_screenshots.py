"""按窗口标题截取 WinUI ScreenLens 窗口（不截桌面）。

用法：
  capture_winui_screenshots.py                    # 默认截当前所有 ScreenLens 窗口
  capture_winui_screenshots.py --resize X Y W H   # 截前先把主窗口重置到指定位置与尺寸
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time
from pathlib import Path
from PIL import ImageGrab

OUT_DIR = Path(__file__).resolve().parent.parent / "docs" / "screenshots"
OUT_DIR.mkdir(parents=True, exist_ok=True)

user32 = ctypes.windll.user32
EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def find_windows(contains):
    found = []

    def cb(hwnd, _lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length == 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buf, length + 1)
        if contains in buf.value:
            found.append((hwnd, buf.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found


def grab_window(hwnd, name, suffix=""):
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    bbox = (rect.left, rect.top, rect.right, rect.bottom)
    if bbox[2] <= bbox[0] or bbox[3] <= bbox[1]:
        print(f"  {name}: 窗口区域无效 {bbox}")
        return None
    img = ImageGrab.grab(bbox=bbox)
    safe = "".join(c for c in name if c.isalnum() or c in "._-")
    path = OUT_DIR / f"{safe}{suffix}.png"
    img.save(path)
    print(f"  {name}  {bbox}  ->  {path.name}  {img.size[0]}x{img.size[1]}")
    return path


def resize_window(hwnd, x, y, w, h):
    SWP_NOZORDER = 0x0004
    SWP_NOACTIVATE = 0x0010
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    user32.SetWindowPos(hwnd, 0, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE)
    print(f"  重置窗口位置为 ({x},{y}) 尺寸 {w}x{h}")
    time.sleep(0.6)


if __name__ == "__main__":
    args = sys.argv[1:]
    resize_args = None
    suffix = ""
    if "--resize" in args:
        idx = args.index("--resize")
        x, y, w, h = map(int, args[idx + 1:idx + 5])
        resize_args = (x, y, w, h)
        suffix = "_resized"
    if "--suffix" in args:
        idx = args.index("--suffix")
        suffix = args[idx + 1]

    for title_kw in ("ScreenLens 设置", "截图选区演示", "识别结果预览"):
        matches = find_windows(title_kw)
        for hwnd, title in matches:
            if title_kw == "ScreenLens 设置" and resize_args:
                resize_window(hwnd, *resize_args)
            grab_window(hwnd, title, suffix)