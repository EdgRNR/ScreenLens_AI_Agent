# -*- coding: utf-8 -*-
"""ScreenLens 配置管理。

配置文件位置：%APPDATA%\\ScreenLens\\config.json
首次运行自动生成默认配置。
"""
import json
import os
import threading

APP_NAME = "ScreenLens"

# 注意：load() 内部会调用 save()，必须使用可重入锁 RLock，
# 否则同一线程二次获取非重入 Lock 会造成死锁。
_LOCK_FACTORY = threading.RLock

DEFAULT_CONFIG = {
    # 全局快捷键（keyboard 库格式，可自行修改，例如 "ctrl+alt+s" / "print_screen"）
    "hotkey": "ctrl+alt+a",
    # 翻译设置
    "translation": {
        # 翻译提供器:
        #   "google_free" - 免费谷歌翻译接口（无需 API Key，需联网，文本会发送到谷歌服务器）
        #   "openai"      - OpenAI 兼容接口（OpenAI / DeepSeek / Gemini 兼容端点等，需配置下方 openai 节点）
        #   "none"        - 关闭在线翻译（完全离线使用）
        "provider": "google_free",
        # 目标语言: "zh"(中文) / "en"(英文) / "ja"(日文)
        "target_language": "zh",
        "openai": {
            "base_url": "https://api.openai.com/v1",
            "api_key": "",
            "model": "gpt-4o-mini",
        },
    },
}


def config_dir() -> str:
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, APP_NAME)


def config_path() -> str:
    return os.path.join(config_dir(), "config.json")


def _deep_merge(base: dict, override: dict) -> dict:
    out = dict(base)
    for k, v in override.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _deep_merge(out[k], v)
        else:
            out[k] = v
    return out


class Config:
    """线程安全的配置对象。"""

    def __init__(self, path: str | None = None):
        self._path = path or config_path()
        self._lock = _LOCK_FACTORY()
        self._data = dict(DEFAULT_CONFIG)
        self.load()

    def load(self) -> None:
        with self._lock:
            if os.path.isfile(self._path):
                try:
                    with open(self._path, "r", encoding="utf-8") as f:
                        user_cfg = json.load(f)
                    self._data = _deep_merge(DEFAULT_CONFIG, user_cfg)
                except (json.JSONDecodeError, OSError):
                    # 配置损坏时保留默认值，不崩溃
                    self._data = dict(DEFAULT_CONFIG)
            else:
                self._data = dict(DEFAULT_CONFIG)
                self.save()

    def save(self) -> None:
        with self._lock:
            os.makedirs(os.path.dirname(self._path), exist_ok=True)
            with open(self._path, "w", encoding="utf-8") as f:
                json.dump(self._data, f, ensure_ascii=False, indent=2)

    @property
    def hotkey(self) -> str:
        return self._data.get("hotkey", DEFAULT_CONFIG["hotkey"])

    @hotkey.setter
    def hotkey(self, value: str) -> None:
        self._data["hotkey"] = value

    @property
    def translation(self) -> dict:
        return self._data.get("translation", DEFAULT_CONFIG["translation"])

    def as_dict(self) -> dict:
        return json.loads(json.dumps(self._data))
