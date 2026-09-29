# -*- coding: utf-8 -*-
"""全局快捷键管理（基于 keyboard 库的低级钩子）。

注意：on_triggered 回调在 keyboard 库的内部线程中被调用，
回调实现必须是线程安全的（例如只往队列里投递任务），
绝不能在回调中直接操作 Tk 控件。
"""
import logging

logger = logging.getLogger(__name__)


class HotkeyManager:
    def __init__(self, on_triggered):
        self._on_triggered = on_triggered
        self._current: str | None = None

    @property
    def current(self) -> str | None:
        return self._current

    def register(self, hotkey: str) -> tuple[bool, str]:
        """注册全局快捷键，替换旧注册，返回 (成功, 消息)。

        失败时旧的注册保持不变。
        """
        if self._current == hotkey:
            return True, hotkey
        try:
            import keyboard

            keyboard.add_hotkey(hotkey, self._fire, suppress=False)
        except Exception as e:
            logger.warning("hotkey register failed (%s): %s", hotkey, e)
            return False, f"快捷键 {hotkey} 注册失败：{e}"
        old = self._current
        if old and old != hotkey:
            try:
                import keyboard

                keyboard.remove_hotkey(old)
            except Exception:
                pass
        self._current = hotkey
        logger.info("hotkey registered: %s", hotkey)
        return True, hotkey

    def unregister(self):
        if self._current:
            hotkey = self._current
            try:
                import keyboard

                keyboard.remove_hotkey(hotkey)
            except Exception:
                pass
            self._current = None

    def try_register(self, hotkey: str) -> tuple[bool, str]:
        """register 的别名：注册新快捷键，失败不影响当前注册。"""
        return self.register(hotkey)

    def _fire(self):
        try:
            logger.info("global hotkey triggered: %s", self._current)
            self._on_triggered()
        except Exception:
            logger.exception("hotkey callback error")

    def shutdown(self):
        self.unregister()
        try:
            import keyboard

            keyboard.unhook_all()
        except Exception:
            pass
