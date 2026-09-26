# -*- coding: utf-8 -*-
"""OpenAI 兼容接口翻译提供器。

适用于任何兼容 /v1/chat/completions 的服务：
OpenAI、DeepSeek、Gemini 兼容端点、本地 Ollama / LM Studio 等。
API Key 只保存在用户本机 config.json，绝不硬编码。
"""
import logging

import requests

from screenlens.translate.provider import (
    LANG_NAMES,
    TranslationError,
    TranslationProvider,
    ProviderNotConfigured,
)

logger = logging.getLogger(__name__)
_TIMEOUT = 30


class OpenAICompatProvider(TranslationProvider):
    name = "openai"

    def __init__(self, cfg: dict):
        self.base_url = (cfg.get("base_url") or "").rstrip("/")
        self.api_key = cfg.get("api_key") or ""
        self.model = cfg.get("model") or "gpt-4o-mini"

    def translate(self, text: str, target_lang: str) -> str:
        text = (text or "").strip()
        if not text:
            return ""
        if not self.base_url or not self.api_key:
            raise ProviderNotConfigured(
                "当前未配置在线翻译服务（请在 config.json 中填写 "
                "translation.openai 的 base_url 与 api_key）。")
        lang = LANG_NAMES.get(target_lang, target_lang)
        messages = [
            {
                "role": "system",
                "content": (
                    f"You are a professional translation engine. "
                    f"Translate the user's text into {lang}. "
                    "Output ONLY the translation, with no explanations, "
                    "no quotes, and preserve original line breaks."
                ),
            },
            {"role": "user", "content": text},
        ]
        url = f"{self.base_url}/chat/completions"
        try:
            resp = requests.post(
                url,
                headers={"Authorization": f"Bearer {self.api_key}",
                         "Content-Type": "application/json"},
                json={"model": self.model, "messages": messages,
                      "temperature": 0},
                timeout=_TIMEOUT,
            )
        except requests.RequestException as e:
            logger.warning("openai_compat network error: %s", e)
            raise TranslationError(
                "翻译失败，请检查网络或服务配置。") from e
        if resp.status_code != 200:
            raise TranslationError(
                f"翻译失败：服务返回 HTTP {resp.status_code}，"
                "请检查 API 配置。")
        try:
            content = resp.json()["choices"][0]["message"]["content"]
        except Exception as e:
            raise TranslationError(
                "翻译失败：服务返回了无法解析的结果。") from e
        result = (content or "").strip()
        if not result:
            raise TranslationError("翻译失败：服务未返回结果。")
        return result
