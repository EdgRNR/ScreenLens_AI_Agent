# -*- coding: utf-8 -*-
"""从仓库根目录 logo.png 生成 WinUI/MSIX 所需图标资源。

源图保持不变；所有输出写入 winui/ScreenLens.WinUI/Assets/。
方形 Logo 在目标画布内保留 8% 安全边距，不拉伸变形。
宽幅资源（SplashScreen / Wide310x150）使用品牌蓝背景 + 居中 Logo 排版。
"""
from PIL import Image
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "logo.png"
OUT = ROOT / "winui" / "ScreenLens.WinUI" / "Assets"
OUT.mkdir(parents=True, exist_ok=True)

# 品牌蓝（与前端 ScreenLensAccentBrush #3D9BFF 一致）
BRAND = (61, 155, 255, 255)

src = Image.open(SRC).convert("RGBA")

def save_square(size: int, name: str, scale_suffix: str = "", margin_ratio: float = 0.08):
    """方形画布内放置 Logo，保留安全边距。"""
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    margin = int(size * margin_ratio)
    inner = size - 2 * margin
    logo = src.resize((inner, inner), Image.LANCZOS)
    canvas.paste(logo, (margin, margin), logo)
    path = OUT / f"{name}{scale_suffix}.png"
    canvas.save(path)
    print(f"  {path.name}  {size}x{size}")

def save_wide(w: int, h: int, name: str, scale_suffix: str = "", margin_ratio: float = 0.0):
    """宽幅画布：品牌蓝背景 + 居中 Logo（按高度适配留边）。"""
    canvas = Image.new("RGBA", (w, h), BRAND)
    inner = int(h * 0.7)
    logo = src.resize((inner, inner), Image.LANCZOS)
    canvas.paste(logo, ((w - inner) // 2, (h - inner) // 2), logo)
    path = OUT / f"{name}{scale_suffix}.png"
    canvas.save(path)
    print(f"  {path.name}  {w}x{h}")

def save_store(w: int, h: int, name: str, margin_ratio: float = 0.06):
    """StoreLogo：透明底居中。"""
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    margin = int(min(w, h) * margin_ratio)
    inner = min(w, h) - 2 * margin
    logo = src.resize((inner, inner), Image.LANCZOS)
    canvas.paste(logo, ((w - inner) // 2, (h - inner) // 2), logo)
    path = OUT / f"{name}.png"
    canvas.save(path)
    print(f"  {path.name}  {w}x{h}")

print("生成 WinUI 图标资源（源 logo.png 未改动）：")

# 窗口内品牌区使用的展示图（28px 显示，2x 生成保证清晰）
save_square(64, "BrandLogo")

# .csproj 引用的 scale-200 资源
save_square(600, "Square150x150Logo", ".scale-200")     # 300x300 @2x
save_square(88, "Square44x44Logo", ".scale-200")        # 44x44 @2x
save_wide(620, 300, "Wide310x150Logo", ".scale-200")    # 310x150 @2x
save_wide(1240, 600, "SplashScreen", ".scale-200")      # 620x300 @2x

# targetsize / StoreLogo / LockScreen
save_square(24, "Square44x44Logo", ".targetsize-24_altform-unplated", margin_ratio=0.0)
save_store(50, 50, "StoreLogo")
save_square(48, "LockScreenLogo", ".scale-200")          # 24x24 @2x

print("完成。")
