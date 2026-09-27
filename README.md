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
| 全局快捷键 | 默认 `Ctrl + Alt + A`，任何应用中均可呼出，设置窗口中录入修改 |
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
2. 屏幕冻结，底部出现工具条：**「▭ 矩形」/「✎ 自由圈选」**（默认矩形），右侧提示当前键位
3. 直接拖动即可框选；**Ctrl+拖动 / 右键拖动** 可临时切到自由圈选（圈一下那句话即可）
   - 拖动中会显示选区边框与 `宽 × 高`；工具条若被选区压住会自动让开
4. 松开鼠标 → 选区进入待确认状态，工具条变为 **「✓ 识别并取词 (Enter)」/「✕ 取消 (Esc)」**
5. 按 `Enter`（或点按钮）开始 OCR，结果面板出现在选区旁；选区过小会提示重新拖动
6. 结果面板操作：**复制**（主操作）/ **翻译**（语言下拉可选中/英/日）/ **重新截图**；关闭点标题栏 `✕` 或按 `Esc`
7. 识别与翻译都在面板中分层显示，文字可直接编辑后再复制 / 翻译；译文卡片有独立「复制译文」

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
- 旧版本配置文件缺少新字段时自动补齐默认值，保持向后兼容

## 6. 技术选型说明

**结论：Python + Tkinter + RapidOCR + keyboard + mss + pystray，PyInstaller 打包。**

| 候选方案 | 结论 | 原因 |
|---|---|---|
| C# WPF + Windows.Media.Ocr | 未选 | 依赖系统 OCR 语言包，中英日识别质量不可控；且环境无 .NET SDK |
| Electron / Tauri | 未选 | 体积大 / 需 Rust 工具链，OCR 仍需外接 |
| PySide6 / Qt（UI 迁移） | 未选（已评估） | 观感更原生，但打包体积 +约 100MB、需重写已通过测试的遮罩/面板/托盘交互与防崩溃逻辑；P0 改版目标在 Tkinter 内可达成（见「UI 改版说明」） |
| **Python + Tkinter（选定）** | — | RapidOCR 内置离线 PP-OCR 模型（中/英/日识别实测优秀，无需语言包）；Tkinter 做 UI 极轻（无 Web 容器）；keyboard 库低级钩子全局热键；mss 截屏；pystray 托盘；开发与维护成本最低 |

各组件角色：

- **截图遮罩**：mss 冻结全屏 → Tkinter 无边框置顶全屏窗 → PIL 生成暗色底图 + 选区清晰层（RGBA alpha 遮罩实现"圈外变暗、圈内清晰"）；工具条以实际测量尺寸定位，按主显示器 / 选区所在显示器避让
- **自由形状处理**：路径最小包围矩形 + 多边形 mask，路径外替换为白底（利于 OCR）
- **OCR**：RapidOCR（PP-OCRv6 det/cls/rec，onnxruntime CPU 推理），启动后台预热，识别在工作线程执行
- **翻译**：`TranslationProvider` 抽象（`GoogleFreeProvider` / `OpenAICompatProvider` / `NoneProvider`），requests/urllib 直连，全部可替换
- **UI 主题**：`ui/theme.py` 统一颜色 / 字体 / 间距与控件状态机（悬停 / 按下 / 禁用），语义状态栏（info / busy / success / error）

### 6.1 UI 改版说明（第三阶段）

技术判断：先评估 Tkinter 能否达成目标外观，而非直接换框架。结论是**保留 Tkinter**——P0 目标（统一主题、显式模式工具条、布局层级、键盘可达、多显示器定位）均可实现；唯一硬性限制是**控件圆角**（Tk 需 Canvas 自绘，收益低于复杂度），故采用 flat + 细描边风格。若后续需要更接近原生的观感，可按「只替换 UI 层、保留 capture/ocr/translate/config」的接口边界迁移 Qt。

关键界面截图见 `docs/screenshots/`：

| 文件 | 内容 |
|---|---|
| `overlay_modes.png` | 遮罩工具条空闲态：矩形 / 自由圈选两个显式入口 + 键位提示 |
| `overlay_detail.png` | 拖动完成后的待确认状态：选区边框、`352 × 160` 尺寸反馈、避让后的确认/取消按钮 |
| `result.png` | 结果面板：标题栏状态指示、识别文字卡片、翻译结果卡片、复制/翻译/重新截图 |
| `settings.png` | 设置窗口：快捷键 / 翻译服务 / 隐私 三张分组卡片 |

重新生成：`python scripts/demo_screenshots.py`。

## 7. 隐私说明（本地 vs 联网）

| 功能 | 是否联网 |
|---|---|
| 截图、矩形/自由圈选 | 完全本地 |
| OCR 识别 | 完全本地（模型离线打包，不上传任何图片） |
| 翻译（google_free / openai） | **联网**：仅识别出的"文本"发送到所选服务；截图本身永不上传；浮窗点击翻译时状态栏会明确提示「文本将发送至在线服务：xxx」 |
| 完全离线使用 | 配置 `"provider": "none"` 即可 |

API Key 仅保存在本机 `config.json`，代码中无任何硬编码密钥。

## 8. 测试说明

运行全部 83 个自动化测试：

```powershell
python -m unittest discover -s tests -v
```

另可运行端到端验收脚本（合成真实热键 → 截图 → OCR → 复制 → 翻译 → 设置 → 退出，会自动备份并恢复用户配置）：

```powershell
python scripts/acceptance_phase2.py
```

覆盖范围：

- **test_region**：矩形/路径包围盒、白底遮罩、alpha 预览、小图放大（9 项）
- **test_ocr**：真实模型识别中/英/日/混合/空图（6 项）
- **test_translate**：Provider 选择、未配置提示、免费接口真实翻译（en↔zh）、网络失败友好报错（11 项）
- **test_config**：默认生成、保存重载、损坏回退、增量合并、结构校验（provider/语言/URL/热键）、原子写入无残留、UTF-8、旧配置自动补齐（18 项）
- **test_gui_integration**：真实 Tk 事件驱动的截图遮罩交互（矩形/自由圈选/工具条模式切换/Enter 确认/工具条按钮确认/微小点击忽略/空 Enter 无副作用/Esc 取消）+ 多显示器布局约束（主显示器定位、避让原空闲位置的选区）+ 结果面板端到端 OCR 展示 + 翻译未启用引导提示（13 项）
- **test_app_smoke**：Win32 API 合成真实按键验证全局热键触发、注册失败回滚、注销；完整应用子进程启动常驻 10 秒不崩溃（3 项）
- **test_phase2_lifecycle**：旧任务迟到结果丢弃、关窗后无悬挂回调、翻译防重复提交、失败后按钮恢复、隐私提示文案、主线程调度器行为（10 项）
- **test_settings_gui**：热键事件→快捷键串解析（修饰键/独立功能键/字母数字拒绝/Esc）、设置保存生效、取消不保存、非法 URL 拦截、热键失败回滚显示、恢复默认、录入流程、API Key 默认隐藏（16 项）

自动化测试不依赖真实 API Key（在线 Provider 测试使用无 Key 的 google_free 且失败可跳过）；已按此原则通过全部用例。

## 9. 已知问题与限制

1. **快捷键冲突检测**：`keyboard` 库为低级键盘钩子，多个程序可同时注册同一快捷键，Windows 本身不互斥，因此无法可靠"检测"冲突。如遇冲突（如 QQ 截图），请在设置窗口更换快捷键。
2. **管理员窗口**：焦点在提权（管理员）应用中时，普通权限的热键钩子收不到按键——此时需以管理员运行 ScreenLens（Windows 通用限制）。
3. **google_free** 为非官方接口，可能随时失效；失败时浮窗会提示「翻译失败，请检查网络或服务配置」。追求稳定请配置 `openai` Provider。
4. **日文识别**：使用 PP-OCR 中日通用模型，常规假名/汉字识别良好，极端艺术字体可能不如专用日文模型。
5. **打包体积**：约 260MB（Python 运行时 + onnxruntime + OpenCV + 离线模型）；采用 onedir 而非单文件，保证常驻启动速度。
6. **多显示器 / 混合 DPI**：遮罩按虚拟屏幕包围盒铺满；浮动工具条改为按**主显示器**（空闲态）或**选区所在显示器**（选区内）定位与避让，已有自动化布局测试覆盖。但**混合 DPI 缩放（如 100% + 200% 拼接）场景仍未实测**（复现步骤：外接不同缩放比例的第二显示器，在扩展屏上按热键圈选，观察遮罩与工具条位置）。
7. **UI 圆角**：Tkinter 无原生圆角控件，界面采用 flat + 细描边风格（详见「UI 改版说明」）；如需圆角需迁移 Qt 或用 Canvas 自绘。
8. **工具条"空闲时弱化"**：Tk 不支持控件级透明度，未实现淡出效果；改为以蓝色高亮当前模式 + 高对比文字表达状态。
9. **选区确认多一步**：第三阶段改为「松开鼠标 → Enter 确认」，以避免误拖动直接触发识别；熟练用户可用 `Ctrl/右键拖动` 圈选后直接 `Enter`。
10. **OCR 历史**：后续计划中的 P1 可选项，为控制范围未实现。
11. **托盘气泡通知**：Windows 通知中心被禁用时气泡可能不显示，会退化为弹窗。

## 10. 项目结构

仓库顶层目录：

```
ScreenLens_AI_Agent/
├── screenlens/             # 现有 Python 应用源码
├── tests/                  # 自动化测试
├── scripts/                # 构建、验收与开发辅助脚本
├── assets/                 # 应用图标等资源
├── docs/
│   ├── plans/              # 项目任务书、阶段计划与 UI 调研
│   └── screenshots/        # 界面截图与验收材料
├── winui/                  # WinUI 原型目录（初始为空，后续在此创建 VS 项目）
├── run.py                  # 源码运行入口
├── ScreenLens.pyw          # 无控制台运行入口
└── requirements.txt        # Python 依赖
```

`winui/` 当前仅预留给新的 C# / WinUI 原型；现有 Python 版仍是可运行主程序。

```
screenlens/
├── app.py                    # 主应用（生命周期、热键→截图→结果 串联、设置应用与热键回滚）
├── config.py                 # 配置（校验 + 原子写入 + 损坏回退提示）
├── dispatch.py               # 主线程调度器（任意线程安全投递 UI 任务）
├── hotkey.py                 # 全局热键管理（注册/替换/回滚）
├── tray.py                   # 系统托盘（回调经主线程调度器）
├── capture/
│   ├── screen.py             # 全屏截图（mss，多显示器）
│   ├── region.py             # 选区处理（包围盒/白底遮罩/放大）
│   └── overlay.py            # 截图遮罩 UI（工具条模式选择/尺寸反馈/Enter 确认/避让定位）
├── ocr/engine.py             # RapidOCR 封装（懒加载+预热）
├── translate/
│   ├── provider.py           # Provider 抽象与注册
│   ├── google_free.py        # 免费谷歌翻译
│   └── openai_compat.py      # OpenAI 兼容接口
└── ui/
    ├── theme.py              # 统一主题（颜色/字体/间距/按钮状态/语义状态栏）
    ├── result_window.py      # OCR 结果面板（任务代际号/防重复提交/隐私提示）
    └── settings_window.py    # 设置窗口（分组卡片/热键录入/Provider/内联反馈）
tests/                        # 83 项自动化测试
docs/screenshots/             # 关键界面截图（验收材料）
scripts/
    ├── build_exe.py          # PyInstaller 打包
    ├── make_icon.py          # 图标生成
    ├── demo_screenshots.py   # 生成关键界面截图
    └── acceptance_phase2.py  # 端到端验收脚本
dist/ScreenLens/ScreenLens.exe  # 可直接运行的成品
```

## 11. 后续建议（P2/P3 方向）

- OCR 历史记录（默认本地、可关闭、提供清空入口）
- 开机自启（注册表 HKCU Run）
- 云端大模型 Provider（视觉理解 / 代码解释 / 公式识别）——Provider 架构已就绪
- 多显示器 / 混合 DPI 实测与适配
