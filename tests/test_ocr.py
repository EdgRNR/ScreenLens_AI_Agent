# -*- coding: utf-8 -*-
"""OCR 引擎测试：中 / 英 / 日 / 混合识别。"""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from PIL import Image, ImageDraw, ImageFont

from screenlens.ocr.engine import OcrEngine

def make_test_image() -> Image.Image:
    """生成含中/英/日/混合文字的标准测试图。"""
    img = Image.new("RGB", (900, 420), "white")
    d = ImageDraw.Draw(img)
    lines = [
        ("Hello, how are you? This is ScreenLens OCR test.", "en"),
        ("今天天气不错，我们一起去公园散步吧！", "zh"),
        ("こんにちは、世界。日本語のテキスト認識テスト。", "ja"),
        ("Mixed 中英文 mixed 混合 line 第123行", "zh"),
    ]
    fonts = {
        "en": r"C:\Windows\Fonts\arial.ttf",
        "zh": r"C:\Windows\Fonts\msyh.ttc",
        "ja": r"C:\Windows\Fonts\YuGothM.ttc",
    }
    y = 30
    for text, lang in lines:
        font_path = fonts[lang]
        try:
            font = ImageFont.truetype(font_path, 32)
        except OSError:
            font = ImageFont.truetype(r"C:\Windows\Fonts\simhei.ttf", 32)
        d.text((40, y), text, fill="black", font=font)
        y += 76
    return img


class TestOcr(unittest.TestCase):
    engine = None

    @classmethod
    def setUpClass(cls):
        cls.engine = OcrEngine()
        cls.engine.warmup_async()
        cls.engine._ensure_engine()

    def _recognize(self):
        with make_test_image() as img:
            return self.engine.recognize(img)

    def test_english(self):
        result = self._recognize()
        joined = " ".join(result.lines)
        self.assertIn("Hello, how are you", joined)
        self.assertIn("ScreenLens", joined)

    def test_chinese(self):
        result = self._recognize()
        joined = "".join(result.lines)
        self.assertIn("今天天气不错", joined)
        self.assertIn("公园散步", joined)

    def test_japanese(self):
        result = self._recognize()
        joined = "".join(result.lines)
        self.assertIn("こんにちは", joined)
        self.assertIn("日本語", joined)

    def test_mixed(self):
        result = self._recognize()
        joined = "".join(result.lines)
        self.assertIn("中英文", joined)
        self.assertIn("123", joined)

    def test_confidence_scores(self):
        result = self._recognize()
        self.assertEqual(len(result.lines), len(result.scores))
        self.assertTrue(all(s > 0.8 for s in result.scores))

    def test_empty_image(self):
        img = Image.new("RGB", (200, 100), "white")
        result = self.engine.recognize(img)
        self.assertTrue(result.is_empty)

    def test_upright_number_line_is_not_rotated_by_marginal_classifier_score(self):
        text = "订单编号 AB01OIl 0123456789 金额 ￥128.50 / 3.14159"
        font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 24)
        img = Image.new("RGB", (int(font.getlength(text)) + 24, 48), "white")
        ImageDraw.Draw(img).text((12, 8), text, font=font, fill="black")
        result = self.engine.recognize(img)
        self.assertIn("订单编号", result.text)
        self.assertIn("0123456789", result.text)
        self.assertIn("3.14159", result.text)
        self.assertEqual(len(result.lines), len(result.boxes))
        for box in result.boxes:
            for x, y in box:
                self.assertGreaterEqual(x, 0)
                self.assertGreaterEqual(y, 0)
                self.assertLessEqual(x, img.width)
                self.assertLessEqual(y, img.height)

    def test_upside_down_chinese_still_recognized(self):
        font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 16)
        img = Image.new("RGB", (500, 40), "white")
        ImageDraw.Draw(img).text((12, 8), "今天天气不错，我们一起去公园散步吧！", font=font, fill="black")
        result = self.engine.recognize(img.rotate(180))
        self.assertIn("今天天气不错", result.text)
        self.assertIn("公园散步", result.text)


if __name__ == "__main__":
    unittest.main()
