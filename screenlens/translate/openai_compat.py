# -*- coding: utf-8 -*-
"""OpenAI-compatible translation, SSE streaming and model discovery."""
import json
from urllib.parse import urlsplit

import requests

from screenlens.translate.provider import (
    LANG_NAMES, TranslationError, TranslationProvider, ProviderNotConfigured,
)

_TIMEOUT = (10, 30)
_MAX_TEXT = 1_000_000


class OpenAICompatProvider(TranslationProvider):
    name = "openai"

    def __init__(self, cfg: dict):
        self.base_url = (cfg.get("base_url") or "").strip().rstrip("/")
        self.api_key = (cfg.get("api_key") or "").strip()
        self.model = (cfg.get("model") or "gpt-4o-mini").strip()

    @property
    def is_configured(self) -> bool:
        return bool(self.base_url) and bool(self.api_key)

    def _check_config(self):
        if not self.is_configured:
            raise ProviderNotConfigured("当前未配置在线翻译服务，请填写 API 地址与密钥。")
        parsed = urlsplit(self.base_url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.query or parsed.fragment:
            raise TranslationError("API 地址格式不正确，请填写服务的基础地址（如 https://api.example.com/v1）。")

    def _headers(self):
        return {"Authorization": f"Bearer {self.api_key}", "Content-Type": "application/json"}

    def _payload(self, text, target_lang):
        lang = LANG_NAMES.get(target_lang, target_lang)
        return {
            "model": self.model,
            "messages": [
                {"role": "system", "content": (
                    f"You are a professional translation engine. Translate the user's text into {lang}. "
                    "Output ONLY the translation, with no explanations, no quotes, and preserve original line breaks."
                )},
                {"role": "user", "content": text},
            ],
            "temperature": 0,
        }

    @staticmethod
    def _check_status(resp):
        if resp.status_code == 200:
            return
        hint = {
            401: "API 密钥无效或已失效", 403: "当前密钥无权访问该服务或模型",
            404: "请检查 API 地址、模型名或服务是否支持此接口",
            429: "请求过于频繁或额度不足",
        }.get(resp.status_code, "请检查服务配置或稍后重试")
        raise TranslationError(f"服务返回 HTTP {resp.status_code}：{hint}。")

    @staticmethod
    def _result(resp):
        try:
            content = resp.json()["choices"][0]["message"]["content"]
            if not isinstance(content, str):
                raise ValueError("content is not text")
        except (ValueError, KeyError, IndexError, TypeError) as e:
            raise TranslationError("翻译失败：服务返回了无法解析的结果。") from e
        result = content.strip()
        if not result:
            raise TranslationError("翻译失败：服务未返回结果。")
        if len(result) > _MAX_TEXT:
            raise TranslationError("译文超过大小限制。")
        return result

    def translate(self, text: str, target_lang: str) -> str:
        text = (text or "").strip()
        if not text:
            return ""
        self._check_config()
        try:
            with requests.post(f"{self.base_url}/chat/completions", headers=self._headers(),
                               json=self._payload(text, target_lang), timeout=_TIMEOUT) as resp:
                self._check_status(resp)
                return self._result(resp)
        except requests.RequestException as e:
            raise TranslationError("翻译失败，请检查网络或服务配置。") from e

    @staticmethod
    def _stream_rejected(resp):
        if resp.status_code not in (400, 405, 415, 422, 501):
            return False
        try:
            error = resp.json().get("error", {})
            if not isinstance(error, dict):
                return False
            message = str(error.get("message", "")).lower()
            return error.get("param") == "stream" or (
                "stream" in message and any(word in message for word in ("support", "disabled", "unavailable", "不支持")))
        except (ValueError, AttributeError):
            return False

    def translate_stream(self, text: str, target_lang: str, on_delta) -> str:
        text = (text or "").strip()
        if not text:
            return ""
        self._check_config()
        payload = self._payload(text, target_lang)
        payload["stream"] = True
        try:
            with requests.post(f"{self.base_url}/chat/completions", headers=self._headers(),
                               json=payload, stream=True, timeout=_TIMEOUT) as resp:
                fallback = self._stream_rejected(resp)
                if not fallback:
                    self._check_status(resp)
                    if "application/json" in resp.headers.get("Content-Type", "").lower():
                        result = self._result(resp)
                        on_delta(result)
                        return result
                    resp.encoding = "utf-8"
                    return self._read_sse(resp, on_delta)
        except requests.RequestException as e:
            raise TranslationError("翻译连接中断，请检查网络后重试。") from e
        # Retry only an explicit rejection before any translation has been emitted.
        result = self.translate(text, target_lang)
        on_delta(result)
        return result

    @staticmethod
    def _read_sse(resp, on_delta):
        parts, event_lines = [], []
        count, finished = 0, False

        def consume(lines):
            nonlocal count, finished
            data = "\n".join(lines)
            if data.strip() == "[DONE]":
                finished = True
                return
            try:
                event = json.loads(data)
                if "error" in event:
                    raise TranslationError("翻译失败：服务在流式输出中返回错误。")
                choices = event.get("choices") or []
                if not choices:
                    return
                choice = next((c for c in choices if c.get("index", 0) == 0), None)
                if choice is None:
                    return
                delta = (choice.get("delta") or {}).get("content")
                if delta:
                    if not isinstance(delta, str):
                        raise ValueError("delta is not text")
                    count += len(delta)
                    if count > _MAX_TEXT:
                        raise TranslationError("译文超过大小限制。")
                    parts.append(delta)
                    on_delta(delta)
                if choice.get("finish_reason") is not None:
                    finished = True
            except (ValueError, TypeError, AttributeError) as e:
                raise TranslationError("翻译失败：流式响应格式不正确。") from e

        for line in resp.iter_lines(chunk_size=1, decode_unicode=True):
            if line.startswith("data:"):
                event_lines.append(line[5:].lstrip(" "))
            elif not line and event_lines:
                consume(event_lines)
                event_lines.clear()
                if finished:
                    break
        if event_lines:
            consume(event_lines)
        result = "".join(parts).strip()
        if not finished:
            raise TranslationError("翻译连接提前结束，已显示的内容可能不完整，请重试。")
        if not result:
            raise TranslationError("翻译失败：服务未返回结果。")
        return result

    def list_models(self) -> list[str]:
        self._check_config()
        try:
            with requests.get(f"{self.base_url}/models", headers=self._headers(), timeout=_TIMEOUT) as resp:
                self._check_status(resp)
                data = resp.json().get("data")
                if not isinstance(data, list):
                    raise ValueError("missing model list")
                models = sorted({item["id"] for item in data if isinstance(item, dict)
                                 and isinstance(item.get("id"), str) and item["id"].strip()})
                if not models:
                    raise TranslationError("服务未返回模型列表，仍可手动填写模型名。")
                return models[:2000]
        except requests.RequestException as e:
            raise TranslationError("获取模型失败，请检查网络或 API 配置；仍可手动填写模型名。") from e
        except (ValueError, AttributeError) as e:
            raise TranslationError("服务未提供兼容的模型列表，仍可手动填写模型名。") from e
