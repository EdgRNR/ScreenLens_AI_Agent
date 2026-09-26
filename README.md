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
| 全局快捷键 | 默认 `Ctrl + Alt + A`，任何应用中均可呼出，可在配置中修改 |
| 矩形截图 | 左键拖动 |
| 自由形状圈选 | `Ctrl + 左键拖动` 或 `右键拖动`，支持圆形 / 椭圆 / 任意曲线 / 不规则多边形 |
| 本地 OCR | 离线识别中文、英文、中英混合、日文，自动阅读顺序排版 |
| 结果浮窗 | 出现在选区旁的轻量无边框浮窗，文字可编辑 |
| 一键复制 | 复制原文 / 复制译文 |
| 翻译 | 中 / 英 / 日目标语言可选，Provider 可替换 |
| 系统托盘 | 常驻托盘图标，菜单支持截图 / 打开配置 / 重载配置 / 退出 |
| 错误处理 | 未识别到文字、翻译失败、API 未配置、配置损坏均有明确提示 |

## 2. 安装与运行

### 方式 A：直接运行打包版（推荐）

1. 打开 `dist/ScreenLens/`
2. 双击 `ScreenLens.exe`
3. 托盘出现镜头图标即已常驻，按 `Ctrl + Alt + A` 开始截图

> 首次启动后台预热 OCR 模型约 1–2 秒；托盘右键 → 退出 可完全关闭。

### 方式 B：源码运行

需要 Python 3.10+（在 3.14 上开发验证）：

```powershell
pip install -r requirements.txt
python run.py            # 控制台模式（可看日志）
python ScreenLens.pyw   # 无控制台模式
```

## 3. 构建（打包 exe）

```powershell
pip install -r requirements.txt pyinstaller
python scripts/build_exe.py
# 输出 dist/ScreenLens/ScreenLens.exe（onedir，约 260MB，含离线 OCR 模型）
```

## 4. 使用说明

1. 按 `Ctrl + Alt + A`（或托盘菜单「截图」）
2. 屏幕冻结并出现暗色遮罩，底部有操作提示
3. **左键拖动** = 矩形选区；**Ctrl+拖动 / 右键拖动** = 自由圈选（圈一下那句话即可）
4. 松开鼠标后自动 OCR，结果浮窗出现在选区旁
5. 浮窗操作：**复制原文** / **翻译**（语言下拉可选中/英/日）/ **重新截图** / **关闭**
6. `Esc` 取消截图或关闭浮窗；识别文字可直接编辑后再复制/翻译

## 5. 配置

配置文件位置：`%APPDATA%\ScreenLens\config.json`（首次运行自动生成，托盘菜单可打开）

```json
{
  "hotkey": "ctrl+alt+a",
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

## 6. 技术选型说明

**结论：Python + Tkinter + RapidOCR + keyboard + mss + pystray，PyInstaller 打包。**

| 候选方案 | 结论 | 原因 |
|---|---|---|
| C# WPF + Windows.Media.Ocr | 未选 | 依赖系统 OCR 语言包，中英日识别质量不可控；且环境无 .NET SDK |
| Electron / Tauri | 未选 | 体积大 / 需 Rust 工具链，OCR 仍需外接 |
| **Python + Tkinter（选定）** | — | RapidOCR 内置离线 PP-OCR 模型（中/英/日识别实测优秀，无需语言包）；Tkinter 做 UI 极轻（无 Web 容器）；keyboard 库低级钩子全局热键；mss 截屏；pystray 托盘；开发与维护成本最低 |

各组件角色：

- **截图遮罩**：mss 冻结全屏 → Tkinter 无边框置顶全屏窗 → PIL 生成暗色底图 + 选区清晰层（RGBA alpha 遮罩实现"圈外变暗、圈内清晰"）
- **自由形状处理**：路径最小包围矩形 + 多边形 mask，路径外替换为白底（利于 OCR）
- **OCR**：RapidOCR（PP-OCRv6 det/cls/rec，onnxruntime CPU 推理），启动后台预热，识别在工作线程执行
- **翻译**：`TranslationProvider` 抽象（`GoogleFreeProvider` / `OpenAICompatProvider` / `NoneProvider`），requests/urllib 直连，全部可替换

## 7. 隐私说明（本地 vs 联网）

| 功能 | 是否联网 |
|---|---|
| 截图、矩形/自由圈选 | 完全本地 |
| OCR 识别 | 完全本地（模型离线打包，不上传任何图片） |
| 翻译（google_free / openai） | **联网**：仅识别出的"文本"发送到所选服务；截图本身永不上传；浮窗点击翻译时状态栏会明确提示「文本将发送至在线服务：xxx」 |
| 完全离线使用 | 配置 `"provider": "none"` 即可 |

API Key 仅保存在本机 `config.json`，代码中无任何硬编码密钥。

## 8. 测试说明

运行全部 40 个自动化测试：

```powershell
python -m unittest discover -s tests -v
```

覆盖范围：

- **test_region**：矩形/路径包围盒、白底遮罩、alpha 预览、小图放大（9 项）
- **test_ocr**：真实模型识别中/英/日/混合/空图（6 项）
- **test_translate**：Provider 选择、未配置提示、免费接口真实翻译（en↔zh）、网络失败友好报错（11 项）
- **test_config**：默认生成、保存重载、损坏回退、增量合并（4 项）
- **test_gui_integration**：真实 Tk 事件驱动的截图遮罩交互（矩形/自由圈选/微小点击忽略/Esc 取消）+ 结果浮窗端到端 OCR 展示 + 未配置翻译提示（7 项）
- **test_app_smoke**：Win32 API 合成真实按键验证全局热键触发；完整应用子进程启动常驻 10 秒不崩溃（3 项）

## 9. 已知问题与限制

1. **快捷键冲突检测**：`keyboard` 库为低级键盘钩子，多个程序可同时注册同一快捷键，Windows 本身不互斥，因此无法可靠"检测"冲突。如遇冲突（如 QQ 截图），请在 `config.json` 修改 `hotkey` 并重载。
2. **管理员窗口**：焦点在提权（管理员）应用中时，普通权限的热键钩子收不到按键——此时需以管理员运行 ScreenLens（Windows 通用限制）。
3. **google_free** 为非官方接口，可能随时失效；失败时浮窗会提示「翻译失败，请检查网络或服务配置」。追求稳定请配置 `openai` Provider。
4. **日文识别**：使用 PP-OCR 中日通用模型，常规假名/汉字识别良好，极端艺术字体可能不如专用日文模型。
5. **打包体积**：约 260MB（Python 运行时 + onnxruntime + OpenCV + 离线模型）；采用 onedir 而非单文件，保证常驻启动速度。
6. **多显示器**：按虚拟屏幕包围盒实现，理论上支持，但仅在单显示器环境实测。
7. 高分屏缩放已处理（Per-Monitor DPI Aware），但 200%+ 缩放的多显示器混合 DPI 场景未实测。

## 10. 项目结构

```
screenlens/
├── app.py                    # 主应用（生命周期、热键→截图→结果 串联）
├── config.py                 # 配置（%APPDATA%\ScreenLens\config.json）
├── hotkey.py                 # 全局热键管理
├── tray.py                   # 系统托盘
├── capture/
│   ├── screen.py             # 全屏截图（mss，多显示器）
│   ├── region.py             # 选区处理（包围盒/白底遮罩/放大）
│   └── overlay.py            # 截图遮罩 UI（矩形+自由圈选）
├── ocr/engine.py             # RapidOCR 封装（懒加载+预热）
├── translate/
│   ├── provider.py           # Provider 抽象与注册
│   ├── google_free.py        # 免费谷歌翻译
│   └── openai_compat.py      # OpenAI 兼容接口
└── ui/result_window.py       # OCR 结果浮窗
tests/                        # 40 项自动化测试
scripts/                      # 构建与图标脚本
dist/ScreenLens/ScreenLens.exe  # 可直接运行的成品
```

## 11. 后续建议（P1/P2 方向）

- 开机自启（注册表 HKCU Run）、快捷键图形化设置界面
- OCR 历史记录、翻译语言自动检测
- 云端大模型 Provider（视觉理解 / 代码解释 / 公式识别）——Provider 架构已就绪
