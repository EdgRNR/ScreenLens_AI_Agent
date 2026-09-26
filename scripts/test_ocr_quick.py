# -*- coding: utf-8 -*-
"""快速验证 RapidOCR 对中/英/日文字的识别效果。"""
import sys
import time
from PIL import Image, ImageDraw, ImageFont

from rapidocr import RapidOCR

FONTS = {
    "zh": r"C:\Windows\Fonts\msyh.ttc",
    "en": r"C:\Windows\Fonts\arial.ttf",
    "ja": r"C:\Windows\Fonts\YuGothM.ttc",
}
FALLBACK_ZH = r"C:\Windows\Fonts\simhei.ttf"


def make_test_image(path):
    img = Image.new("RGB", (900, 420), "white")
    d = ImageDraw.Draw(img)
    lines = [
        ("Hello, how are you? This is ScreenLens OCR test.", "en"),
        ("今天天气不错，我们一起去公园散步吧！", "zh"),
        ("こんにちは、世界。日本語のテキスト認識テスト。", "ja"),
        ("Mixed 中英文 mixed 混合 line 第123行", "zh"),
        ("The quick brown fox jumps over the lazy dog 0123456789", "en"),
    ]
    y = 30
    for text, lang in lines:
        font_path = FONTS.get(lang, FALLBACK_ZH)
        try:
            font = ImageFont.truetype(font_path, 32)
        except OSError:
            font = ImageFont.truetype(FALLBACK_ZH, 32)
        d.text((40, y), text, fill="black", font=font)
        y += 76
    img.save(path)


def main():
    t0 = time.time()
    ocr = RapidOCR()
    print(f"OCR init: {time.time() - t0:.2f}s")

    img_path = "tests/data/ocr_test.png"
    import os

    os.makedirs("tests/data", exist_ok=True)
    make_test_image(img_path)
    t0 = time.time()
    result = ocr(img_path)
    print(f"OCR run: {time.time() - t0:.2f}s")
    print("type:", type(result).__name__)
    print("attrs:", [a for a in dir(result) if not a.startswith("_")])
    print("boxes:", 0 if result.boxes is None else len(result.boxes))
    if result.txts:
        for t, s in zip(result.txts, (result.scores or [None] * len(result.txts))):
            print(f"  [{s if s is None else round(float(s), 3)}] {t}")
    else:
        print("NO TEXT FOUND")
        print(result)


if __name__ == "__main__":
    main()
