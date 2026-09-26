# -*- coding: utf-8 -*-
"""免费谷歌翻译接口（translate.googleapis.com，无需 API Key）。

隐私提示：使用该提供器时，仅识别出的"文本"会被发送到谷歌服务器，
截图本身不会上传。完全离线可在配置中将 provider 设为 "none"。
"""
import logging
import urllib.parse
import urllib.request

from screenlens.translate.provider import (
    LANG_CODES,
    TranslationError,
    TranslationProvider,
)

logger = logging.getLogger(__name__)

_ENDPOINT = "https://translate.googleapis.com/translate_a/single"
_TIMEOUT = 10


class GoogleFreeProvider(TranslationProvider):
    name = "google_free"

    def translate(self, text: str, target_lang: str) -> str:
        text = (text or "").strip()
        if not text:
            return ""
        tl = LANG_CODES.get(target_lang, "zh-CN")
        params = urllib.parse.urlencode({
            "client": "gtx",
            "sl": "auto",
            "tl": tl,
            "dt": "t",
            "q": text,
        })
        url = f"{_ENDPOINT}?{params}"
        req = urllib.request.Request(
            url, headers={"User-Agent": "Mozilla/5.0 (ScreenLens)"})
        try:
            with urllib.request.urlopen(req, timeout=_TIMEOUT) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
        except Exception as e:
            logger.warning("google_free translate failed: %s", e)
            raise TranslationError(
                "翻译失败，请检查网络或服务配置。") from e

        import json
        try:
            data = json.loads(raw)
            # 返回结构: [[["译文","原文",...],...],...]
            segments = data[0]
            parts = [seg[0] for seg in segments if seg and seg[0]]
            result = "".join(parts).strip()
        except Exception as e:
            raise TranslationError(
                "翻译失败：服务返回了无法解析的结果。") from e
        if not result:
            raise TranslationError("翻译失败：服务未返回结果。")
        return result
