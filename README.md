# ScreenLens

> 按下快捷键 → 随手圈一下屏幕上的内容 → 自动识别 → 立即复制或翻译。

Windows 桌面轻量取词工具：**自由圈选 → 本地 OCR → 复制 / 翻译**。

- 目标平台：Windows 10 / Windows 11
- 产品形态：常驻托盘的后台工具
- 核心链路全部离线可用（翻译为可选在线功能）

---

## 1. 功能总览

| 功能 | 说明 |
|---|---|
| 全局快捷键 | 默认 ``Ctrl + ` ``，任何应用中均可呼出，设置窗口中录入修改 |
| 矩形截图 | 遮罩工具条选「矩形」（默认），左键拖动 |
| 自由形状圈选 | 遮罩工具条选「自由圈选」，或用 `Ctrl + 左键拖动` / `右键拖动` 临时切换，支持圆形 / 椭圆 / 任意曲线 / 不规则多边形 |
| 截图反馈 | 拖动中显示选区边框与 `宽 × 高` 尺寸；工具条自动避让选区与屏幕边缘 |
| 选区确认 | 松开鼠标后按 `Enter` 或点「识别并取词」完成；`Esc` /「取消」放弃；选区过小提示重试 |
| 本地 OCR | 离线识别中文、英文、中英混合、日文，自动阅读顺序排版 |
| 结果面板 | 出现在选区旁，含标题栏（状态指示 + 关闭）、识别文字卡片、翻译结果卡片，长文本可滚动 |
| 一键复制 | 复制原文 / 复制译文，写入剪贴板后校验，失败有提示 |
| 翻译 | 中 / 英 / 日目标语言可选，Provider 可替换；请求中按钮禁用，防重复提交 |
| 图形设置窗口 | 托盘菜单「设置」：分组卡片式布局，热键录入、翻译服务、API 配置、目标语言，内联反馈保存结果 |
| 统一视觉 | 单一主题层（颜色 / 字体 / 间距 / 按钮状态 / 语义状态栏）；设置页跟随系统深浅色 |
| 系统托盘 | 常驻托盘图标，菜单支持截图 / 设置 / 打开配置 / 重载配置 / 退出 |
| 错误处理 | 未识别到文字、翻译失败、翻译未启用、API 未配置、配置损坏均有明确提示（图标 + 颜色） |
| 异步安全 | OCR/翻译在工作线程执行；旧任务迟到结果自动丢弃；窗口关闭/重截图/退出不留悬挂回调 |
| 键盘可达 | `Enter` 确认选区、`Esc` 取消 / 关闭面板、按钮支持悬停与禁用状态 |

## 2. 安装与运行

### 当前开发方式：WinUI 前端 + Python 后台代理

开发测试时，先在终端运行 `.venv\Scripts\python.exe run_agent.py`，再从 Visual Studio 按 F5 启动 WinUI 前端。前端只连接已有 Agent；如果后台没启动，设置界面仍可打开并显示未连接状态，不会擅自拉起 Python 进程。

### 旧版 Python/Tk 原型运行方式（历史）

仓库中的 `run_legacy.py`、`run_legacy.pyw` 和 `dist/ScreenLens/ScreenLens.exe` 属于旧版 Python/Tk 原型，不是当前 WinUI 应用入口。

WinUI 与 Python Agent 的单独发布打包流程尚未提供。发布包应包含 WinUI 前端和配套 Agent/Worker；不要用旧脚本生成的 exe 作为当前 WinUI 版本。

旧原型源码启动命令（仅用于维护旧版）：

```powershell
pip install -r requirements.txt
python run_legacy.py       # 控制台模式（可看日志）
python run_legacy.pyw      # 无控制台模式
```

### 架构：WinUI 前端 + 后台代理（当前开发形态）

界面由 WinUI 3 前端提供，业务与 OCR 留在 Python 侧，两者以命名管道通信。
进程各自按需存活，空闲时不驻留重内存：

| 组件 | 职责 | 内存（私有工作集） |
|---|---|---|
| 后台代理 `run_agent.py` | 常驻托盘；全局热键、配置读写、命名管道服务、唤起前端。不含 UI 框架，不加载 OCR 模型 | 目标低于 **50 MiB**；须实测确认 |
| WinUI 前端 `ScreenLens.WinUI.exe` | 设置窗口 / 截图选区 / 结果窗口；窗口关闭后进程退出 | 按需测量，不计入“仅后台空闲”指标 |
| OCR / 翻译 worker | 任务期间按需启动；空闲 30 秒后退出，OCR 模型随进程退出回收 | 任务期间升高；空闲态不应残留 |

OCR 固定使用 RapidOCR 3.9.2、PP-OCRv6 small 检测/识别模型及 ONNX Runtime CPU 推理。同一 worker 内复用引擎；首次请求仍需加载模型。截图检测采用长边限制，避免把狭长选区按短边过度放大，并对倒置方向判定使用更高置信度门槛。配置、比较结果与复测命令见 [OCR 优化说明](docs/OCR_PIPELINE.md)。

### 用 Visual Studio 启动（推荐）

打开 `winui\ScreenLens.WinUI\ScreenLens.WinUI.slnx`，选择 **x64 / Unpackaged** 后按 F5。
开发时需先单独运行后台代理，再启动前端：

```powershell
.venv\Scripts\python.exe run_agent.py
```

WinUI 会连接已运行的后台代理；关闭设置窗口后，前端退出而托盘代理继续工作；从托盘
打开设置或按全局热键时，已有 WinUI 实例会被唤起。

只有当正式发布目录将 `ScreenLensAgent.exe` 与 WinUI 放在同一目录时，前端才会自动启动随包代理。也可通过 `SCREENLENS_AGENT_EXE` 显式指定代理，或在开发调试时设置 `SCREENLENS_AUTOSTART_AGENT=1` 选择自动启动。

手动运行后台也方便观察日志：

```powershell
# 开发调试时单独启动后台
.venv\Scripts\python.exe run_agent.py
```

- 前端 exe 定位顺序：环境变量 `SCREENLENS_WINUI_EXE` → 代理同目录（打包布局）→
  仓库开发布局 `winui\ScreenLens.WinUI\bin\x64\{Debug,Release}\...\ScreenLens.WinUI.exe`
- IPC 协议（命名管道 v1，仅当前用户可访问，不监听任何 TCP 端口）见
  `screenlens/ipc/protocol.py`
- 设置数据边界：热键 / 翻译服务等写入 Python 配置；主题、卡片密度、截图交互等
  纯前端偏好写入 `%LOCALAPPDATA%\ScreenLens\frontend.json`，互不覆盖
- 日志：`%LOCALAPPDATA%\ScreenLens\logs\{agent,worker}.log`

内存采样会自动统计指定代理及其 worker / WinUI 后代进程；从日志中的代理 PID 开始：

```powershell
.venv\Scripts\python.exe scripts\measure_memory.py --root-pid <代理PID> --duration 60 --label 后台空闲
```

截图模式的命令行参数（供脚本化调用与自动化验证）：

```powershell
ScreenLens.WinUI.exe --capture --region=400,140,900,80 --auto
```

`--region=x,y,w,h` 为相对虚拟屏幕的物理像素矩形，`--auto` 表示预置选区后立即识别。

> **抓屏能力边界（已知）**：选区截图由 GDI `BitBlt`（`SRCCOPY | CAPTUREBLT`）
> 从虚拟屏幕 DC 取得，覆盖全部显示器，内存开销低。但带
> `WS_EX_NOREDIRECTIONBITMAP` 的窗口像素不写入重定向表面，**无法被截取**，
> 典型如 Microsoft Edge、Windows 设置、Windows Terminal（含其承载的
> `cmd`）、部分 UWP/WinUI 应用。传统 Win32 窗口、Electron 应用（VS Code、
> 多数 IDE）以及未启用系统 backdrop 的 WinUI 3 窗口不受影响。
> 若需覆盖全部窗口，应改用 Windows.Graphics.Capture（WGC）或
> DXGI Desktop Duplication。

> **调试开关**：设置环境变量 `SCREENLENS_DEBUG_DUMP=<目录>` 后，前端会把
> 每次实际发往 OCR 的选区 PNG 落盘，便于定位“识别结果为空”。
> 未设置该变量时不产生任何文件与额外开销。

## 3. 旧版原型打包（非当前 WinUI 发布包）

```powershell
pip install -r requirements.txt pyinstaller
python scripts/build_exe.py
# 输出旧版 Python/Tk 原型 dist/ScreenLens/ScreenLens.exe
```

## 4. 使用说明

1. 开发调试时先运行 `.venv\Scripts\python.exe run_agent.py`，再在 Visual Studio 按 F5。发布版的自动启动行为待发布打包完成后验收。
2. 按 ``Ctrl + ` ``，或从托盘菜单启动截图。屏幕冻结后拖动选择区域，按 `Enter` 识别，按 `Esc` 取消。
3. 默认矩形选区；设置中可选择自由圈选和松开鼠标后的确认行为。
4. 识别结果窗口可复制文本、调用当前配置的翻译服务、重新截图；关闭设置/结果窗口不会关闭托盘代理。
5. OpenAI 兼容翻译需要在设置中配置服务地址、模型和 API Key；未配置或请求失败时会显示错误信息。

## 5. 设置与配置

### 图形设置窗口（推荐）

托盘图标右键 → **设置**，无需手工编辑 JSON：

- **快捷键**：点击「录入新快捷键」后直接按下组合键（如 `Ctrl+Alt+S`），保存前自动校验格式；注册失败会自动保留原快捷键并说明原因
- **翻译服务**：Google 免费接口 / OpenAI 兼容接口 / 关闭翻译
- **OpenAI 兼容配置**：Base URL、API Key（默认隐藏，可切换显示）、模型
- **默认目标语言**：中文 / 英文 / 日文
- **保存并生效**（无需重启）/ **取消** / **恢复默认值**

### 配置文件

配置文件位置：`%APPDATA%\ScreenLens\config.json`（首次运行自动生成，托盘菜单可打开）。
保存采用临时文件 + 原子替换，不会因写入中断而损坏；读取时自动校验结构，损坏或非法配置会回退默认值并在启动时提示。

```json
{
  "hotkey": "ctrl+`",
  "translation": {
    "provider": "google_free",
    "target_language": "zh",
    "openai": {
      "base_url": "https://api.openai.com/v1",
      "api_key": "",
      "model": "gpt-4o-mini"
    }
  }
}
```

- `hotkey`：keyboard 库格式，如 `ctrl+alt+s`、`print_screen` 等，修改后托盘「重载配置」生效
- `translation.provider`：
  - `"google_free"` — 免费谷歌翻译接口，无需 Key（默认）
  - `"openai"` — 任意 OpenAI 兼容服务（OpenAI / DeepSeek / Gemini 兼容端点 / 本地 Ollama 等），需填写 `openai.base_url` + `openai.api_key` + `openai.model`
  - `"none"` — 关闭在线翻译，完全离线
- `target_language`：`zh` / `en` / `ja`（浮窗下拉会临时覆盖）
- 旧版本配置文件缺少新字段时自动补齐默认值，保持向后兼容

## 6. 技术选型说明

**当前代码形态：WinUI 3（C#）前端 + Python 后台代理 + 按需 OCR/翻译 worker。**

WinUI 负责设置、选区、结果显示与剪贴板；后台代理负责托盘、快捷键、配置和命名管道 IPC；worker 在任务期间加载 OCR/翻译依赖并在空闲后退出。Python/Tkinter 是早期原型实现，下面保留的历史说明不代表当前 WinUI 界面或运行流程。

| 组件 | 当前职责 |
|---|---|
| WinUI 3 / C# | 设置窗口、截图遮罩、结果窗口与系统剪贴板 |
| Python Agent | 托盘、全局热键、配置读写、命名管道服务、按需启动前端 |
| C# OCR Worker | 构建后优先使用；离线 OCR、按需启动、空闲 30 秒退出 |
| Python Worker | 翻译任务及未构建 C# worker 时的 OCR 回退 |
| IPC | 当前用户限定的 Windows 命名管道，带版本号、请求 ID 和长度帧 |

### 6.1 早期 Tkinter 原型记录（历史）

C# OCR worker 的构建、回退与验证步骤见 [OCR_WORKER.md](docs/OCR_WORKER.md)。

本节只记录旧版 Python/Tkinter 原型背景；当前 UI 以 `winui/ScreenLens.WinUI` 为准。

旧版 UI 调研材料仅作历史参考；其中描述的 Tk 控件、交互按钮和运行流程不代表当前 WinUI 版本。旧 Tk 原型截图已清理，避免与当前界面验收图混淆。

当前 WinUI 页面与端到端验收截图见 `docs/screenshots/`；历史计划统一归档在 [`docs/PLAN.md`](docs/PLAN.md)。

## 7. 隐私说明（本地 vs 联网）

| 功能 | 是否联网 |
|---|---|
| 截图、矩形/自由圈选 | 完全本地 |
| OCR 识别 | 完全本地（模型离线打包，不上传任何图片） |
| 翻译（google_free / openai） | **联网**：仅识别出的"文本"发送到所选服务；截图本身永不上传；浮窗点击翻译时状态栏会明确提示「文本将发送至在线服务：xxx」 |
| 完全离线使用 | 配置 `"provider": "none"` 即可 |

API Key 仅保存在本机 `config.json`，代码中无任何硬编码密钥。

## 8. 测试说明

运行 Python 自动化测试：

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests -v
```

旧版 Python/Tk 前端的端到端验收脚本仍在仓库中，不能代替当前 WinUI 的手工验收：

```powershell
python scripts/acceptance_phase2.py
```

覆盖范围：

目前自动化用例主要覆盖 Python 配置、OCR、翻译、IPC 帧协议和 Agent 逻辑；WinUI 窗口、截图交互、多显示器 DPI 与真实在线 API 流程需要在 Windows 桌面环境手工验收。不要把旧测试数量或旧验收脚本结果当作当前 UI 的完整验收结论。

## 9. 已知问题与限制

1. **快捷键冲突检测**：`keyboard` 库为低级键盘钩子，多个程序可同时注册同一快捷键，Windows 本身不互斥，因此无法可靠"检测"冲突。如遇冲突（如 QQ 截图），请在设置窗口更换快捷键。
2. **管理员窗口**：焦点在提权（管理员）应用中时，普通权限的热键钩子收不到按键——此时需以管理员运行 ScreenLens（Windows 通用限制）。
3. **google_free** 为非官方接口，可能随时失效；失败时浮窗会提示「翻译失败，请检查网络或服务配置」。追求稳定请配置 `openai` Provider。
4. **日文识别**：使用 PP-OCR 中日通用模型，常规假名/汉字识别良好，极端艺术字体可能不如专用日文模型。
5. **打包体积**：约 260MB（Python 运行时 + onnxruntime + OpenCV + 离线模型）；采用 onedir 而非单文件，保证常驻启动速度。
6. **多显示器 / 混合 DPI**：遮罩按虚拟屏幕包围盒铺满；浮动工具条改为按**主显示器**（空闲态）或**选区所在显示器**（选区内）定位与避让，已有自动化布局测试覆盖。但**混合 DPI 缩放（如 100% + 200% 拼接）场景仍未实测**（复现步骤：外接不同缩放比例的第二显示器，在扩展屏上按热键圈选，观察遮罩与工具条位置）。
7. **截图兼容性**：当前 WinUI 截图由 GDI BitBlt 实现；启用受保护/无重定向表面的应用可能无法被截取，详见前文抓屏边界说明。
8. **桌面验收**：混合 DPI、多显示器切换、系统缩放和后台托盘唤起需在目标 Windows 设备实测。
11. **托盘气泡通知**：Windows 通知中心被禁用时气泡可能不显示，会退化为弹窗。

## 10. 项目结构

仓库顶层目录：

```
ScreenLens_AI_Agent/
├── screenlens/             # Python Agent、Worker、IPC 与业务模块
├── winui/                  # WinUI 3 前端与 Visual Studio 项目
├── tests/                  # 自动化测试
├── scripts/                # 构建、验收、测量与开发辅助脚本
├── logo.png                # 品牌 Logo，后台托盘与图标生成共用
├── docs/
│   ├── PLAN.md             # 唯一的项目计划、当前状态与历史计划归档
│   └── screenshots/        # 当前 WinUI 页面与端到端验收截图
├── run_agent.py            # 当前 Python 后台代理（控制台）
├── run_agent.pyw           # 当前 Python 后台代理（无控制台）
├── run_legacy.py           # 旧版 Tk 原型入口（控制台）
├── run_legacy.pyw          # 旧版 Tk 原型入口（无控制台）
└── requirements.txt        # Python 依赖
```

Python/Tk 源码保留为旧版原型；当前 Windows 桌面 UI 开发入口为 WinUI 解决方案。
后续实施事项统一维护在 [`docs/PLAN.md`](docs/PLAN.md)，避免在 README 和多个计划文件中重复维护。
