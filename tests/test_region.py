# -*- coding: utf-8 -*-
"""选区处理逻辑单元测试（纯函数，无 GUI）。"""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from PIL import Image

from screenlens.capture import region


def make_screen() -> Image.Image:
    """黑底白格测试图，便于验证裁剪与遮罩。"""
    img = Image.new("RGB", (300, 200), (255, 255, 255))
    # 中间画一个 100x60 的黑色矩形 (100,70)-(200,130)
    for x in range(100, 201):
        for y in range(70, 131):
            img.putpixel((x, y), (0, 0, 0))
    return img


class TestRegion(unittest.TestCase):
    def test_rect_bbox(self):
        self.assertEqual(region.rect_bbox((10, 20), (30, 5)), (10, 5, 30, 20))
        self.assertEqual(region.rect_bbox((0, 0), (0, 0)), (0, 0, 0, 0))

    def test_path_bbox(self):
        pts = [(5, 5), (100, 5), (100, 80), (5, 80)]
        self.assertEqual(region.path_bbox(pts), (5, 5, 100, 80))

    def test_bbox_valid(self):
        self.assertTrue(region.bbox_valid((0, 0, 20, 20)))
        self.assertFalse(region.bbox_valid((0, 0, 5, 50)))
        self.assertFalse(region.bbox_valid((0, 0, 50, 5)))

    def test_crop_rect_padding(self):
        screen = make_screen()
        crop = region.crop_rect(screen, (100, 70, 200, 130))
        # padding=6 → (100-6, 70-6, 200+6, 130+6) = 112x72
        self.assertEqual(crop.size, (112, 72))

    def test_crop_rect_clamped_at_edge(self):
        screen = make_screen()
        crop = region.crop_rect(screen, (295, 195, 300, 200))
        self.assertEqual(crop.size, (5 + 6, 5 + 6))  # 右/下被屏幕边界截断

    def test_crop_freeform_masks_outside(self):
        """自由圈选：路径外应为白色，路径内保留原像素。"""
        screen = make_screen()
        # 圈住黑色矩形：椭圆路径
        import math

        cx, cy, rx, ry = 150, 100, 60, 40
        pts = [(cx + rx * math.cos(a / 20 * 2 * math.pi),
                cy + ry * math.sin(a / 20 * 2 * math.pi)) for a in range(20)]
        pts = [(int(x), int(y)) for x, y in pts]
        crop = region.crop_freeform(screen, pts)
        # 中心（路径内）应是黑色
        self.assertEqual(crop.getpixel(
            (crop.width // 2, crop.height // 2)), (0, 0, 0))
        # 四角（路径外）应是白色
        self.assertEqual(crop.getpixel((2, 2)), (255, 255, 255))
        self.assertEqual(crop.getpixel((crop.width - 3, crop.height - 3)),
                         (255, 255, 255))

    def test_crop_freeform_bbox_content(self):
        """包裹矩形外的原内容必须被白底替换。"""
        screen = make_screen()
        # 圈选右下角一小块白色区域，路径不包含黑色矩形
        pts = [(250, 170), (280, 170), (280, 190), (250, 190)]
        crop = region.crop_freeform(screen, pts)
        # 整个裁剪结果不应包含黑色像素（黑矩形在路径外）
        ext = crop.getextrema()
        for lo, _ in ext:
            self.assertGreater(lo, 200)

    def test_make_alpha_preview(self):
        screen = make_screen()
        # 菱形路径：中心在路径内，四角在路径外
        pts = [(150, 70), (200, 100), (150, 130), (100, 100)]
        prev = region.make_alpha_preview(screen, pts)
        self.assertEqual(prev.mode, "RGBA")
        # 中心不透明
        self.assertEqual(prev.getpixel((50, 30))[3], 255)
        # 角落透明
        self.assertEqual(prev.getpixel((2, 2))[3], 0)
        self.assertEqual(prev.getpixel((prev.width - 3, prev.height - 3))[3], 0)

    def test_upscale_for_ocr(self):
        small = Image.new("RGB", (30, 20))
        big = region.upscale_for_ocr(small)
        self.assertGreaterEqual(big.height, 48)
        self.assertGreaterEqual(big.width, 48)

        big_img = Image.new("RGB", (300, 200))
        self.assertIs(region.upscale_for_ocr(big_img), big_img)


if __name__ == "__main__":
    unittest.main()
