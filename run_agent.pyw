# -*- coding: utf-8 -*-
"""双击启动入口：用 pythonw 运行后台代理（无控制台窗口）。

等价于 ``python run_agent.py``，仅便于在资源管理器中直接双击。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from screenlens.agent.app import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
