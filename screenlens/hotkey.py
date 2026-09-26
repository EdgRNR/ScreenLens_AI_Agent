# -*- coding: utf-8 -*-
"""全局快捷键管理（基于 keyboard 库的低级钩子）。"""
import logging
import threading

logger = logging.getLogger(__name__)


class HotkeyManager:
    def __init__(self, on_triggered):
        self._on_triggered = on_triggered
        self._current: str | None = None

    def register(self, hotkey: str) -> tuple[bool, str]:
        """注册全局快捷键，返回 (成功, 消息)。"""
        self.unregister()
        try:
            import keyboard
            keyboard.add_hotkey(hotkey, self._fire,
                                suppress=False)
            self._current = hotkey
            logger.info("hotkey registered: %s", hotkey)
            return True, hotkey
        except Exception as e:
            self._current = None
            logger.warning("hotkey register failed (%s): %s", hotkey, e)
            return False, f"快捷键 {hotkey} 注册失败：{e}"

    def unregister(self):
        if self._current:
            try:
                import keyboard

                keyboard.remove_hotkey(self._current)
            except Exception:
                pass
            self._current = None

    def _fire(self):
        """keyboard 库的回调在其内部线程执行，转交到 UI 线程。"""
        try:
            threading.Thread(target=self._on_triggered, daemon=True).start()
        except Exception:
            logger.exception("hotkey callback error")

    def shutdown(self):
        self.unregister()
        try:
            import keyboard

            keyboard.unhook_all()
        except Exception:
            pass
