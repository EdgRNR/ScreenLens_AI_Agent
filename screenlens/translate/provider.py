# -*- coding: utf-8 -*-
"""翻译 Provider 架构。

所有翻译提供器实现 translate(text, target_lang) -> str。
- GoogleFreeProvider : 免费谷歌翻译接口（无需 Key，需联网）
- OpenAICompatProvider : 任何 OpenAI 兼容 chat/completions 端点
  （OpenAI / DeepSeek / Gemini 兼容端点 / 本地 Ollama 等）
- NoneProvider : 未配置 / 已关闭在线翻译
"""
import abc

LANG_NAMES = {"zh": "中文", "en": "英文", "ja": "日文"}
LANG_CODES = {"zh": "zh-CN", "en": "en", "ja": "ja"}  # google 用


class TranslationError(Exception):
    """翻译失败（网络/服务/配置问题）。"""


class ProviderNotConfigured(TranslationError):
    """未配置在线翻译服务。"""


class TranslationProvider(abc.ABC):
    name = "base"

    @abc.abstractmethod
    def translate(self, text: str, target_lang: str) -> str:
        """翻译文本到目标语言，失败时抛 TranslationError。"""


class NoneProvider(TranslationProvider):
    """表示"没有可用的翻译服务"。"""

    name = "none"

    def translate(self, text: str, target_lang: str) -> str:
        raise ProviderNotConfigured("当前未配置在线翻译服务。")


def get_provider(translation_cfg: dict) -> TranslationProvider:
    """根据配置返回翻译提供器实例。"""
    provider_name = (translation_cfg or {}).get("provider", "none")
    if provider_name == "google_free":
        from screenlens.translate.google_free import GoogleFreeProvider

        return GoogleFreeProvider()
    if provider_name == "openai":
        from screenlens.translate.openai_compat import OpenAICompatProvider

        return OpenAICompatProvider(translation_cfg.get("openai", {}))
    return NoneProvider()
