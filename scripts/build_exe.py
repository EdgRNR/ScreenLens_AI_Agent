# -*- coding: utf-8 -*-
"""PyInstaller 打包脚本：构建 ScreenLens 独立可执行程序。

用法:
    python scripts/build_exe.py
输出:
    dist/ScreenLens/ScreenLens.exe   （onedir，启动快，适合常驻工具）
"""
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
ICON = os.path.join(ROOT, "logo.png")


def main():
    import PyInstaller.__main__

    args = [
        os.path.join(ROOT, "run_legacy.py"),
        "--name=ScreenLens",
        "--noconfirm",
        "--clean",
        "--windowed",                    # 无控制台（常驻托盘工具）
        "--collect-all", "rapidocr",     # 打包离线 OCR 模型
        "--collect-all", "pystray",
        "--collect-submodules", "screenlens",
        "--add-data", ICON + os.pathsep + ".",
        "--icon", ICON,                  # PyInstaller 使用 Pillow 转换为 ICO
        "--distpath", os.path.join(ROOT, "dist"),
        "--workpath", os.path.join(ROOT, "build"),
        "--specpath", ROOT,
    ]
    PyInstaller.__main__.run(args)
    print("\nBuild OK -> dist/ScreenLens/ScreenLens.exe")


if __name__ == "__main__":
    main()
