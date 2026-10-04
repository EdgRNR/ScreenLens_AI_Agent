# -*- coding: utf-8 -*-
"""系统托盘图标（pystray）。

注意：pystray 菜单回调在托盘线程执行，一律通过 app.post()
投递到 Tk 主线程，不在回调中直接操作 Tk。
"""
import logging
from pathlib import Path

from PIL import Image

logger = logging.getLogger(__name__)


def _make_icon_image() -> Image.Image:
    """从品牌 logo.png 创建托盘图标，兼容源码和 PyInstaller 运行。"""
    # PyInstaller sets __file__ inside its bundle; logo.png is bundled at
    # the same root as the screenlens package, independent of the working dir.
    logo_path = Path(__file__).resolve().parent.parent / "logo.png"
    with Image.open(logo_path) as source:
        logo = source.convert("RGBA")

    # Ignore faint alpha noise when trimming the transparent outer margin,
    # so the actual artwork remains readable at Windows tray icon sizes.
    bounds = logo.getchannel("A").point(lambda alpha: 255 if alpha > 8 else 0).getbbox()
    if bounds is None:
        raise ValueError("logo.png 全透明，无法创建托盘图标")
    logo = logo.crop(bounds)
    side = max(logo.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(logo, ((side - logo.width) // 2, (side - logo.height) // 2))
    return square.resize((64, 64), Image.Resampling.LANCZOS)


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
