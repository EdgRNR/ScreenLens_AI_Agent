# ScreenLens WinUI 原型

ScreenLens 的 C# / WinUI 3 界面原型（第四阶段：前端界面与演示交互）。

- Python 程序仍位于仓库根目录及 `screenlens/`，保持不变、互不影响。
- 本原型只完成界面结构与交互演示，**不接入** OCR、截图、翻译后端，也不与 Python 通信；所有设置仅保存在进程内存中。

## 项目结构

```text
winui/ScreenLens.WinUI/
├── ScreenLens.WinUI.slnx         # 解决方案（单项目）
├── App.xaml(.cs)                 # 应用级资源与入口（含崩溃日志输出）
├── MainWindow.xaml(.cs)          # 设置主窗口：NavigationView 左侧导航 + 右侧滚动内容
├── Styles/ThemeResources.xaml    # 视觉基线：深/浅双主题、强调色、间距、卡片样式
├── Controls/SettingCard.xaml     # 可复用设置卡片（图标+名称+说明+控件同行）
├── ViewModels/DemoSettings.cs    # 演示状态单例（INPC，仅内存）
├── Views/
│   ├── Settings/                 # 七个设置页：通用/截图与选区/OCR 与结果/翻译/快捷键/外观/关于
│   └── Preview/                  # 截图选区演示窗口 + 识别结果演示窗口
└── Assets/                       # 应用图标
```

## 启动方法

**Visual Studio**：打开 `ScreenLens.WinUI/ScreenLens.WinUI.slnx`，直接 F5。

**命令行**：

```powershell
cd winui\ScreenLens.WinUI
dotnet run -c Debug -p:Platform=x64
```

或先构建再运行产物：

```powershell
dotnet build -c Debug -p:Platform=x64
# 产物位置
bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\ScreenLens.WinUI.exe
```

- 目标框架：`net8.0-windows10.0.19041.0`，Windows App SDK（以 `.csproj` 为准，未升级）。
- 项目已启用 `WindowsAppSDKSelfContained`：**目标机器无需安装** Windows App SDK Runtime 即可运行（未打包 unpackaged 方式启动）。

## 演示流程

1. 主窗口默认深色主题；「通用」或「外观」页切换主题模式即时预览。
2. 侧栏页脚「打开截图选区演示」→ 在 XAML 绘制的模拟桌面上拖拽绘制选区（实时尺寸标签、遮罩挖洞、浮动工具条）。
3. 工具条**确认**（或 Enter）→ 打开识别结果预览：原文/译文分层，点「翻译」有 1.5 秒模拟延迟，点「复制」仅本地提示。
4. 任意演示窗口按 **Esc** 关闭；未接入的功能统一提示「原型演示，尚未接入功能」。

## 明确未接入的功能

截图捕获、OCR、翻译服务与网络请求、系统热键注册、托盘、剪贴板读写、设置持久化、与 Python 后端的一切通信。阶段边界详见 `docs/plans/ScreenLens_WinUI_前端阶段计划书.md`。
