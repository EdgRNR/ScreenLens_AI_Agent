# -*- coding: utf-8 -*-
"""翻译 Provider 测试：未配置错误、免费接口、OpenAI 兼容接口。"""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from screenlens.translate.provider import (
    LANG_NAMES,
    ProviderNotConfigured,
    TranslationError,
    get_provider,
)


class TestProviderSelection(unittest.TestCase):
    def test_none_provider_by_default(self):
        p = get_provider({"provider": "none"})
        self.assertEqual(p.name, "none")

    def test_none_provider_when_missing(self):
        p = get_provider({})
        self.assertEqual(p.name, "none")

    def test_none_provider_raises_not_configured(self):
        p = get_provider({"provider": "none"})
        with self.assertRaises(ProviderNotConfigured):
            p.translate("hello", "zh")

    def test_openai_not_configured(self):
        p = get_provider({"provider": "openai", "openai": {}})
        with self.assertRaises(ProviderNotConfigured):
            p.translate("hello", "zh")

    def test_google_free_selected(self):
        p = get_provider({"provider": "google_free"})
        self.assertEqual(p.name, "google_free")

    def test_lang_names(self):
        self.assertEqual(LANG_NAMES["zh"], "中文")
        self.assertEqual(LANG_NAMES["en"], "英文")
        self.assertEqual(LANG_NAMES["ja"], "日文")


class TestGoogleFree(unittest.TestCase):
    """联网测试。无网络环境自动跳过。"""

    def test_translate_en_to_zh(self):
        p = get_provider({"provider": "google_free"})
        try:
            result = p.translate("Hello, how are you?", "zh")
        except TranslationError as e:
            self.skipTest(f"网络不可用: {e}")
        self.assertTrue(result)
        self.assertTrue(any(c > "\u4e00" for c in result),
                        f"应包含中文字符: {result}")

    def test_translate_zh_to_en(self):
        p = get_provider({"provider": "google_free"})
        try:
            result = p.translate("你好，世界。", "en")
        except TranslationError as e:
            self.skipTest(f"网络不可用: {e}")
        self.assertTrue(result)

    def test_translate_empty(self):
        p = get_provider({"provider": "google_free"})
        self.assertEqual(p.translate("", "zh"), "")

    def test_bad_url_raises_friendly(self):
        from screenlens.translate import google_free as gf

        gf._ENDPOINT = "https://invalid.invalid.screenlens.test/x"
        try:
            p = gf.GoogleFreeProvider()
            with self.assertRaises(TranslationError) as cm:
                p.translate("hello", "zh")
            self.assertIn("翻译失败", str(cm.exception))
        finally:
            gf._ENDPOINT = "https://translate.googleapis.com/translate_a/single"


class TestOpenAICompat(unittest.TestCase):
    def test_missing_key_message(self):
        from screenlens.translate.openai_compat import OpenAICompatProvider

        p = OpenAICompatProvider({"base_url": "https://x/v1", "api_key": ""})
        with self.assertRaises(ProviderNotConfigured) as cm:
            p.translate("hi", "zh")
        self.assertIn("未配置", str(cm.exception))


if __name__ == "__main__":
    unittest.main()
