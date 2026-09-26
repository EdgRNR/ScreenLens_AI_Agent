# -*- coding: utf-8 -*-
"""选区处理：自由形状路径 → 最小包围矩形 + 路径外白底遮罩。"""
from PIL import Image, ImageDraw

# 视为有效选区的最小尺寸（像素）
MIN_REGION_SIZE = 8
# 包围矩形向外扩的边距，避免文字贴边被裁掉
PADDING = 6


def rect_bbox(p0: tuple[int, int], p1: tuple[int, int]) -> tuple[int, int, int, int]:
    """两个对角点 → (x0, y0, x1, y1)。"""
    return min(p0[0], p1[0]), min(p0[1], p1[1]), max(p0[0], p1[0]), max(p0[1], p1[1])


def path_bbox(points: list[tuple[int, int]]) -> tuple[int, int, int, int]:
    """路径点列表 → 最小包围矩形 (x0, y0, x1, y1)。"""
    if not points:
        return 0, 0, 0, 0
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def bbox_size(bbox: tuple[int, int, int, int]) -> tuple[int, int]:
    return bbox[2] - bbox[0], bbox[3] - bbox[1]


def bbox_valid(bbox: tuple[int, int, int, int], min_size: int = MIN_REGION_SIZE) -> bool:
    w, h = bbox_size(bbox)
    return w >= min_size and h >= min_size


def crop_rect(screen: Image.Image, bbox: tuple[int, int, int, int],
              padding: int = PADDING) -> Image.Image:
    """按包围矩形裁剪（矩形截图模式）。"""
    x0, y0, x1, y1 = bbox
    x0 = max(0, x0 - padding)
    y0 = max(0, y0 - padding)
    x1 = min(screen.width, x1 + padding)
    y1 = min(screen.height, y1 + padding)
    return screen.crop((x0, y0, x1, y1))


def crop_freeform(screen: Image.Image, points: list[tuple[int, int]],
                  padding: int = PADDING) -> Image.Image:
    """自由形状裁剪：取路径最小包围矩形，路径之外的区域替换为白色。

    screen 传入前应已经裁剪到虚拟屏幕坐标系（点坐标与屏幕图像同源）。
    """
    if len(points) < 3:
        # 退化情况：按矩形处理
        return crop_rect(screen, path_bbox(points), padding)

    x0, y0, x1, y1 = path_bbox(points)
    x0 = max(0, x0 - padding)
    y0 = max(0, y0 - padding)
    x1 = min(screen.width, x1 + padding)
    y1 = min(screen.height, y1 + padding)
    if x1 <= x0 or y1 <= y0:
        return screen.crop((x0, y0, max(x1, x0 + 1), max(y1, y0 + 1)))

    crop = screen.crop((x0, y0, x1, y1))
    rel_points = [(px - x0, py - y0) for px, py in points]

    mask = Image.new("L", crop.size, 0)
    ImageDraw.Draw(mask).polygon(rel_points, fill=255)

    out = Image.new("RGB", crop.size, (255, 255, 255))
    out.paste(crop, (0, 0), mask)
    return out


def make_alpha_preview(screen: Image.Image, points: list[tuple[int, int]]) -> Image.Image:
    """给遮罩层预览用：路径内部保持清晰，路径外透明（透出暗色背景）。"""
    x0, y0, x1, y1 = path_bbox(points)
    x0, y0 = max(0, x0), max(0, y0)
    x1 = min(screen.width, x1)
    y1 = min(screen.height, y1)
    crop = screen.crop((x0, y0, x1, y1)).convert("RGBA")
    rel_points = [(px - x0, py - y0) for px, py in points]
    mask = Image.new("L", crop.size, 0)
    ImageDraw.Draw(mask).polygon(rel_points, fill=255)
    crop.putalpha(mask)
    return crop


def upscale_for_ocr(img: Image.Image, min_height: int = 48,
                    min_width: int = 48) -> Image.Image:
    """小图放大，提升 OCR 对小字号文字的识别率。"""
    scale = 1.0
    if img.height < min_height:
        scale = max(scale, min_height / img.height)
    if img.width < min_width:
        scale = max(scale, min_width / img.width)
    if scale <= 1.01:
        return img
    scale = min(scale, 4.0)
    return img.resize((int(img.width * scale), int(img.height * scale)),
                      Image.LANCZOS)
