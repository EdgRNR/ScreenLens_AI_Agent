# -*- coding: utf-8 -*-
"""系统托盘图标（pystray）。"""
import logging

from PIL import Image, ImageDraw

logger = logging.getLogger(__name__)


def _make_icon_image() -> Image.Image:
    """绘制一个简洁的"取景镜头"托盘图标。"""
    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    accent = (0, 194, 255, 255)
    dark = (30, 31, 36, 255)
    d.ellipse((6, 6, 46, 46), fill=dark, outline=accent, width=4)
    d.ellipse((20, 20, 32, 32), fill=accent)
    d.line((42, 42, 58, 58), fill=accent, width=6)
    return img


def make_menu(app):
    import pystray

    def _capture(icon, item):
        app.request_capture()

    def _open_config(icon, item):
        app.open_config_file()

    def _reload(icon, item):
        app.reload_config()

    def _about(icon, item):
        app.show_about()

    def _quit(icon, item):
        app.quit()

    return pystray.Menu(
        pystray.MenuItem("截图（全局快捷键）", _capture, default=True),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem(lambda item: f"快捷键：{app.hotkey_display}",
                         None, enabled=False),
        pystray.MenuItem("打开配置文件", _open_config),
        pystray.MenuItem("重载配置", _reload),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("关于 ScreenLens", _about),
        pystray.MenuItem("退出", _quit),
    )


def run_tray(app):
    """在独立线程中运行托盘图标。"""
    import pystray

    try:
        icon = pystray.Icon(
            "ScreenLens", _make_icon_image(), "ScreenLens", make_menu(app))
        app.tray_icon = icon
        icon.run_detached()
        logger.info("tray icon started")
        return True
    except Exception:
        logger.exception("tray icon failed to start")
        return False
