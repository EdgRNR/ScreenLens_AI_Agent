# -*- coding: utf-8 -*-
"""从仓库根目录 logo.png 生成 WinUI / MSIX 所需图标资源。

源图保持不变；所有输出写入 winui/ScreenLens.WinUI/Assets/。

第七阶段修复要点（原实现只改像素尺寸，图形视觉占比过小）：
1. 先裁掉源图的透明边距，取图形有效包围盒，再补成正方形；
   "导出像素尺寸够大"不等于"图形在画布中视觉尺寸够大"。
2. 按用途分级留白：任务栏 / 标题栏用到的小尺寸几乎不留白，
   让图形在 Windows 图标安全区内尽可能饱满、清晰。
3. 额外输出多尺寸 ScreenLens.ico（16→256），供窗口图标、任务栏与
   exe 内嵌图标使用（PNG 压缩的 ICO，Vista+ 原生支持）。
"""
from PIL import Image
from pathlib import Path
import io
import struct

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "logo.png"
OUT = ROOT / "winui" / "ScreenLens.WinUI" / "Assets"
OUT.mkdir(parents=True, exist_ok=True)

# 品牌蓝（与前端 ScreenLensAccentBrush #3D9BFF 一致）
BRAND = (61, 155, 255, 255)

# Windows 图标安全区：图形落在画布 ~90% 以内不会与相邻图标"粘连"，
# 但要尽量少留白才能与同排应用图标视觉尺寸接近。
SQUARE_MARGIN = 0.04      # 常规方形图标
SMALL_MARGIN = 0.02       # ≤48px 的小图标
TIGHT_MARGIN = 0.0        # ≤32px 的任务栏 / 标题栏图标


def load_trimmed_square(alpha_threshold: int = 8) -> Image.Image:
    """裁掉源图透明边距并补成正方形（不裁切图形、不改变比例）。

    注意：直接对 alpha 用 getbbox() 会被图像边缘的极淡噪点影响，
    返回几乎整幅画布，等于没有裁剪；这里用阈值屏蔽噪点后再取包围盒。
    """
    full = Image.open(SRC).convert("RGBA")
    alpha = full.split()[3]
    solid = alpha.point(lambda v: 255 if v > alpha_threshold else 0)
    bbox = solid.getbbox()
    if bbox is None:
        raise SystemExit("logo.png 全透明，无法生成图标")
    trimmed = full.crop(bbox)
    w, h = trimmed.size
    side = max(w, h)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(trimmed, ((side - w) // 2, (side - h) // 2), trimmed)
    print(f"源图 {full.size[0]}x{full.size[1]} -> 有效图形包围盒 {bbox} "
          f"({w}x{h}) -> 正方形 {side}x{side}")
    return square


SRC_SQUARE = load_trimmed_square()


def margin_for(size: int) -> float:
    if size <= 32:
        return TIGHT_MARGIN
    if size <= 48:
        return SMALL_MARGIN
    return SQUARE_MARGIN


def render_square(size: int, margin_ratio: float | None = None) -> Image.Image:
    """方形画布内放置 Logo，按尺寸分级保留安全边距。"""
    if margin_ratio is None:
        margin_ratio = margin_for(size)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    margin = int(round(size * margin_ratio))
    inner = size - 2 * margin
    logo = SRC_SQUARE.resize((inner, inner), Image.LANCZOS)
    canvas.paste(logo, (margin, margin), logo)
    return canvas


def save_square(size: int, name: str, scale_suffix: str = "", margin_ratio: float | None = None):
    path = OUT / f"{name}{scale_suffix}.png"
    render_square(size, margin_ratio).save(path)
    used = margin_for(size) if margin_ratio is None else margin_ratio
    print(f"  {path.name}  {size}x{size}  边距 {used:.0%}")


def save_wide(w: int, h: int, name: str, scale_suffix: str = "", logo_ratio: float = 0.78):
    """宽幅画布：品牌蓝背景 + 居中 Logo（保持方形比例，不拉伸）。"""
    canvas = Image.new("RGBA", (w, h), BRAND)
    inner = int(h * logo_ratio)
    logo = SRC_SQUARE.resize((inner, inner), Image.LANCZOS)
    canvas.paste(logo, ((w - inner) // 2, (h - inner) // 2), logo)
    path = OUT / f"{name}{scale_suffix}.png"
    canvas.save(path)
    print(f"  {path.name}  {w}x{h}  logo {inner}px")


def save_ico(path: Path, sizes: list[int]):
    """合成多尺寸 ICO（每个尺寸独立渲染，避免统一缩放导致小尺寸发糊）。"""
    blobs = []
    for size in sizes:
        img = render_square(size)
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        blobs.append((size, buf.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = bytearray()
    payload = bytearray()
    for size, blob in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        payload += blob
    path.write_bytes(header + bytes(entries) + bytes(payload))
    print(f"  {path.name}  {len(blobs)} 个尺寸 {sizes}")


print("生成 WinUI 图标资源（源 logo.png 未改动）：")

# 窗口内品牌区 / 关于页展示图（28–56 DIP 显示；基准 64px + 高 DPI 变体）
save_square(64, "BrandLogo")
save_square(128, "BrandLogo", ".scale-200")

# .csproj 引用的 scale-200 资源
save_square(600, "Square150x150Logo", ".scale-200")     # 300x300 @2x
save_square(88, "Square44x44Logo", ".scale-200")        # 44x44 @2x
save_wide(620, 300, "Wide310x150Logo", ".scale-200")    # 310x150 @2x
save_wide(1240, 600, "SplashScreen", ".scale-200")      # 620x300 @2x

# targetsize / StoreLogo / LockScreen
save_square(24, "Square44x44Logo", ".targetsize-24_altform-unplated")
save_square(50, "StoreLogo")
save_square(48, "LockScreenLogo", ".scale-200")          # 24x24 @2x

# 窗口 / 任务栏 / exe 内嵌图标
save_ico(OUT / "ScreenLens.ico", [16, 20, 24, 32, 40, 48, 64, 128, 256])

print("完成。")
