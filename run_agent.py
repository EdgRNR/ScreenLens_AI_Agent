# -*- coding: utf-8 -*-
"""ScreenLens 后台代理入口（headless，无界面框架）。

用法::

    .venv\\Scripts\\python.exe run_agent.py

启动后常驻系统托盘，本身不显示任何窗口：

- 全局快捷键（默认 Ctrl+Alt+A）唤起 WinUI 截图流程；
- 托盘菜单可触发截图、打开设置、退出；
- 设置与截图任务经命名管道（``\\\\.\\pipe\\ScreenLensAgent``）通信；
- OCR / 翻译由按需启动的短生命周期 worker 子进程完成，
  空闲时代理仅占约 20 MiB 私有内存。

旧版 Tk 界面入口为 ``run_legacy.py``；当前 WinUI 应用使用本文件作为后台代理。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from screenlens.agent.app import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
