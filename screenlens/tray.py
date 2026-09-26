# -*- coding: utf-8 -*-
"""系统托盘图标（pystray）。

注意：pystray 菜单回调在托盘线程执行，一律通过 app.post()
投递到 Tk 主线程，不在回调中直接操作 Tk。
"""
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
        app.post(app.request_capture)

    def _settings(icon, item):
        app.post(app.open_settings)

    def _open_config(icon, item):
        app.post(app._open_config_file_impl)

    def _reload(icon, item):
        app.post(app._reload_config_impl)

    def _about(icon, item):
        app.post(app.show_about)

    def _quit(icon, item):
        app.post(app.quit)

    return pystray.Menu(
        pystray.MenuItem("截图（全局快捷键）", _capture, default=True),
        pystray.MenuItem("设置…", _settings),
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
