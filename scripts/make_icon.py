# -*- coding: utf-8 -*-
"""生成应用图标（托盘/ICO）：取景镜头。"""
import os

from PIL import Image, ImageDraw


def make_icon(size: int = 256) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    accent = (0, 194, 255, 255)
    dark = (30, 31, 36, 255)
    s = size / 64.0
    d.ellipse((6 * s, 6 * s, 46 * s, 46 * s), fill=dark, outline=accent,
              width=max(1, int(4 * s)))
    d.ellipse((20 * s, 20 * s, 32 * s, 32 * s), fill=accent)
    d.line((42 * s, 42 * s, 58 * s, 58 * s), fill=accent,
           width=max(1, int(6 * s)))
    return img


if __name__ == "__main__":
    assets = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "assets")
    os.makedirs(assets, exist_ok=True)
    icon = make_icon()
    icon.save(os.path.join(assets, "icon.ico"),
              sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128),
                     (256, 256)])
    print("icon written to", os.path.join(assets, "icon.ico"))
