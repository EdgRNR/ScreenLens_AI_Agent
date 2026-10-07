# -*- coding: utf-8 -*-
"""ScreenLens 配置管理。

配置文件位置：%APPDATA%\\ScreenLens\\config.json
- 首次运行自动生成默认配置
- 保存采用同目录临时文件 + os.replace 原子替换，避免写入中断留下损坏文件
- 读取时校验结构，损坏/无效配置回退默认值并记录 load_error
- 旧版部分配置自动补齐缺失字段（向后兼容）
"""
import json
import logging
import os
import tempfile
import threading

logger = logging.getLogger(__name__)

APP_NAME = "ScreenLens"

VALID_PROVIDERS = ("google_free", "openai", "none")
VALID_LANGS = ("zh", "en", "ja")

DEFAULT_CONFIG = {
    "startup": {"start_minimized": True},
    # 全局快捷键（keyboard 库格式，可自行修改，例如 "ctrl+alt+s" / "print_screen"）
    "hotkey": "ctrl+`",
    "hotkeys": {"capture_translate": "", "capture_ocr": "", "settings": "", "cancel_capture": "esc"},
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

# 注意：load() 内部会调用 save()，必须使用可重入锁 RLock，
# 否则同一线程二次获取非重入 Lock 会造成死锁。
_LOCK_FACTORY = threading.RLock


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


def validate_config(data) -> list[str]:
    """校验配置结构，返回错误列表（空列表 = 合法）。

    仅做结构/取值校验；快捷键能否被系统接受由注册时的
    HotkeyManager.register() 负责反馈。
    """
    errors = []
    if not isinstance(data, dict):
        return ["配置根节点必须是对象"]

    startup = data.get("startup", {})
    if not isinstance(startup, dict):
        errors.append("startup 必须是对象")
    elif not isinstance(startup.get("start_minimized", True), bool):
        errors.append("启动时仅显示托盘必须是布尔值")

    hk = data.get("hotkey", DEFAULT_CONFIG["hotkey"])
    if not isinstance(hk, str) or not hk.strip():
        errors.append("快捷键不能为空")
    elif len(hk) > 100:
        errors.append("快捷键格式不正确")

    shortcuts = data.get("hotkeys", {})
    if not isinstance(shortcuts, dict):
        errors.append("hotkeys 必须是对象")
    else:
        for action, value in shortcuts.items():
            if action not in DEFAULT_CONFIG["hotkeys"]:
                errors.append("未知快捷键操作")
            if not isinstance(value, str) or len(value) > 100:
                errors.append("快捷键格式不正确")

    tr = data.get("translation", {})
    if not isinstance(tr, dict):
        errors.append("translation 必须是对象")
        return errors

    provider = tr.get("provider", DEFAULT_CONFIG["translation"]["provider"])
    if provider not in VALID_PROVIDERS:
        errors.append(f"翻译提供器必须是 {' / '.join(VALID_PROVIDERS)} 之一")

    lang = tr.get("target_language",
                  DEFAULT_CONFIG["translation"]["target_language"])
    if lang not in VALID_LANGS:
        errors.append("目标语言必须是 zh / en / ja 之一")

    oa = tr.get("openai", {})
    if not isinstance(oa, dict):
        errors.append("translation.openai 必须是对象")
    else:
        base_url = oa.get("base_url", "")
        if base_url and not isinstance(base_url, str):
            errors.append("base_url 必须是字符串")
        elif base_url and not (base_url.startswith("http://")
                               or base_url.startswith("https://")):
            errors.append("base_url 必须以 http:// 或 https:// 开头")
        if not isinstance(oa.get("api_key", ""), str):
            errors.append("api_key 必须是字符串")
        if not isinstance(oa.get("model", ""), str):
            errors.append("model 必须是字符串")

        # 选择 OpenAI 兼容时缺参数，应在保存阶段就报错，
        # 而不是等用户截图翻译时才失败。
        if provider == "openai":
            if not (base_url or "").strip():
                errors.append("选择 OpenAI 兼容时需填写 API 地址（base_url）")
            if not (oa.get("model") or "").strip():
                errors.append("选择 OpenAI 兼容时需填写模型名（model）")
            if not (oa.get("api_key") or "").strip():
                errors.append("选择 OpenAI 兼容时需填写 API 密钥（api_key）")
    return errors


class Config:
    """线程安全的配置对象。"""

    def __init__(self, path: str | None = None):
        self._path = path or config_path()
        self._lock = _LOCK_FACTORY()
        self._data = dict(DEFAULT_CONFIG)
        # 非空表示上次加载发现问题（已回退默认值），供 UI 提示
        self.load_error: str | None = None
        self.load()

    def load(self) -> None:
        with self._lock:
            self.load_error = None
            if os.path.isfile(self._path):
                try:
                    with open(self._path, "r", encoding="utf-8") as f:
                        user_cfg = json.load(f)
                    if not isinstance(user_cfg, dict):
                        raise ValueError("配置根节点必须是对象")
                    errors = validate_config(user_cfg)
                    if errors:
                        merged = _deep_merge(DEFAULT_CONFIG, user_cfg)
                        self._data = merged
                        self.load_error = "；".join(errors)
                        return
                    self._data = _deep_merge(DEFAULT_CONFIG, user_cfg)
                except (json.JSONDecodeError, ValueError) as e:
                    self._data = dict(DEFAULT_CONFIG)
                    self.load_error = f"配置文件损坏（{e}），已回退默认配置"
                    return
                except OSError as e:
                    self._data = dict(DEFAULT_CONFIG)
                    self.load_error = f"配置文件无法读取（{e}），已回退默认配置"
                    return
            else:
                self._data = dict(DEFAULT_CONFIG)
                self.save()

    def save(self) -> None:
        """原子写入：先写同目录临时文件，再 os.replace 替换。"""
        with self._lock:
            os.makedirs(os.path.dirname(self._path), exist_ok=True)
            self._atomic_write()

    def _atomic_write(self):
        data = json.dumps(self._data, ensure_ascii=False, indent=2)
        fd, tmp_path = tempfile.mkstemp(
            prefix=".screenlens-", suffix=".tmp",
            dir=os.path.dirname(self._path))
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(data)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp_path, self._path)
        except Exception:
            try:
                os.remove(tmp_path)
            except OSError:
                pass
            raise

    @property
    def path(self) -> str:
        return self._path

    @property
    def hotkey(self) -> str:
        return self._data.get("hotkey", DEFAULT_CONFIG["hotkey"])

    @hotkey.setter
    def hotkey(self, value: str) -> None:
        self._data["hotkey"] = value

    @property
    def translation(self) -> dict:
        return self._data.get("translation", DEFAULT_CONFIG["translation"])

    def set_translation(self, translation: dict) -> None:
        """整体替换 translation 节点（设置窗口保存时使用）。"""
        self._data["translation"] = translation

    def as_dict(self) -> dict:
        return json.loads(json.dumps(self._data))

    def apply_dict(self, data: dict) -> None:
        """用提交的配置替换已知字段，保留磁盘上的未知字段后原子写回。

        供 IPC SaveSettings 使用：磁盘文件里可能有本版本不认识的
        字段（更高版本或手工编辑添加），整体替换会静默丢失它们，
        因此以磁盘当前内容为基底做深度合并。
        """
        with self._lock:
            disk: dict = {}
            if os.path.isfile(self._path):
                try:
                    with open(self._path, "r", encoding="utf-8") as f:
                        disk = json.load(f)
                except (json.JSONDecodeError, OSError):
                    disk = {}
            if not isinstance(disk, dict):
                disk = {}
            previous = self._data
            self._data = _deep_merge(disk, data)
            try:
                self.save()
            except Exception:
                self._data = previous
                raise
