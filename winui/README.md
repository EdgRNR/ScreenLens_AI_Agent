# ScreenLens WinUI

当前 Windows 桌面前端，使用 C#、XAML、WinUI 3。Python Agent 与它通过本机命名管道连接。

## 开发运行

1. 在仓库根目录启动后台代理：`.venv\Scripts\python.exe run_agent.py`。
2. 用 Visual Studio 打开 `ScreenLens.WinUI.slnx`，选 x64 / Unpackaged 后按 F5。
3. F5 只启动 WinUI；未启动 Agent 时前端显示未连接状态。这个双进程调试方式是有意设计的。

## 当前结构

- `App.xaml(.cs)`、`MainWindow.xaml(.cs)`：应用入口与设置主窗口。
- `Views/Settings/`：设置页面。
- `Views/Capture/`、`Views/Result/`：截图选区与识别结果页面。
- `Services/`：Agent 连接、IPC、设置访问与前端逻辑。
- `Controls/`、`Styles/`：可复用控件与主题资源。

目前前后端集成已进入可运行阶段；发布打包、低内存数值验收和多显示器 DPI 桌面验收仍应以仓库根目录 [`docs/PLAN.md`](../docs/PLAN.md) 的当前待办为准。视觉布局已基本获得用户认可，改动应集中在明确的功能问题和缺陷修复。