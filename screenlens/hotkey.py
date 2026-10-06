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
        self._handle = None

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

            # keyboard's nonblocking key-up dispatch observes the key after it
            # has left the pressed-key set, so release-triggered combinations
            # can register successfully without ever firing. Use key-down.
            handle = keyboard.add_hotkey(hotkey, self._fire, suppress=False)
        except Exception as e:
            logger.warning("hotkey register failed (%s): %s", hotkey, e)
            return False, f"快捷键 {hotkey} 注册失败：{e}"
        old = self._current
        if old and old != hotkey:
            try:
                import keyboard

                keyboard.remove_hotkey(self._handle)
            except Exception:
                pass
        self._current = hotkey
        self._handle = handle
        logger.info("hotkey registered: %s", hotkey)
        return True, hotkey

    def unregister(self):
        if self._current:
            try:
                import keyboard

                keyboard.remove_hotkey(self._handle)
            except Exception:
                pass
            self._current = None
            self._handle = None

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


class ShortcutRegistry:
    """Register an entire shortcut set transactionally, including rollback."""

    def __init__(self, callbacks):
        self._callbacks = callbacks
        self._managers = {}
        self._signatures = {}

    def apply(self, shortcuts):
        signatures = set()
        action_signatures = {}
        try:
            import keyboard

            for action, value in shortcuts.items():
                if action not in self._callbacks:
                    return False, "未知快捷键操作"
                if not value:
                    continue
                parts = [part.strip().lower() for part in value.split("+")]
                if len(parts) == 1 and not (parts[0].startswith("f") and parts[0][1:].isdigit()
                        or parts[0] in ("print screen", "print_screen")
                        or action == "cancel_capture" and parts[0] in ("esc", "escape")):
                    return False, "请使用组合键或功能键，避免影响正常文字输入"
                steps = keyboard.parse_hotkey(value)
                if len(steps) != 1:
                    return False, "请使用一个快捷键组合，不支持连续按键序列"
                signature = tuple(sorted(tuple(sorted(codes)) for codes in steps[0]))
                if signature in signatures:
                    return False, "快捷键重复，请为不同操作选择不同的按键"
                signatures.add(signature)
                action_signatures[action] = signature
        except Exception as e:
            return False, f"快捷键格式不正确：{e}"

        staged = {}
        for action, value in shortcuts.items():
            # Esc remains a local capture-window key, never a global hook.
            if not value or (action == "cancel_capture" and value.lower() in ("esc", "escape")):
                continue
            # Reuse the hook for an equivalent gesture, even if it moves to a
            # different action. keyboard cannot safely register the same string
            # twice while an older remover still refers to that registration.
            old = next((manager for old_action, manager in self._managers.items()
                        if self._signatures.get(old_action) == action_signatures[action]), None)
            if old is not None:
                staged[action] = old
                continue
            manager = HotkeyManager(self._callbacks[action])
            ok, message = manager.register(value)
            if not ok:
                for candidate in staged.values():
                    if candidate not in self._managers.values():
                        candidate.unregister()
                return False, message
            staged[action] = manager
        for action, old in self._managers.items():
            if old not in staged.values():
                old.unregister()
        for action, manager in staged.items():
            manager._on_triggered = self._callbacks[action]
        self._managers = staged
        self._signatures = action_signatures
        return True, ""

    def shutdown(self):
        for manager in self._managers.values():
            manager.unregister()
        self._managers.clear()
        self._signatures.clear()
