# -*- coding: utf-8 -*-
"""PyInstaller 打包脚本：构建 ScreenLens 独立可执行程序。

用法:
    python scripts/build_exe.py
输出:
    dist/ScreenLens/ScreenLens.exe   （onedir，启动快，适合常驻工具）
"""
import os
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
ICON = os.path.join(ROOT, "assets", "icon.ico")


def main():
    if not os.path.exists(ICON):
        subprocess.check_call([sys.executable,
                               os.path.join(ROOT, "scripts", "make_icon.py")])

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
        "--distpath", os.path.join(ROOT, "dist"),
        "--workpath", os.path.join(ROOT, "build"),
        "--specpath", ROOT,
    ]
    if os.path.exists(ICON):
        args += ["--icon", ICON]

    PyInstaller.__main__.run(args)
    print("\nBuild OK -> dist/ScreenLens/ScreenLens.exe")


if __name__ == "__main__":
    main()
