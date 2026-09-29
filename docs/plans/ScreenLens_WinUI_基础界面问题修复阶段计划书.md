# ScreenLens WinUI 基础界面问题修复阶段计划书

> 阶段：第七阶段——设置卡片、品牌标识与运行验收
> 状态：已完成（2026-09-28，编译通过、运行逐页验收并留档截图，详见第 7 节执行记录）
> 范围：只修复 WinUI 前端基础 UI 问题；不接入 Python 后端、OCR、截图、翻译 API 或系统设置。

## 1. 阶段目标

先解决当前截图中可见的基础界面缺陷，再做一轮所有页面的一致性检查。目标是每项设置都能完整呈现图标、标题、说明与操作控件，品牌 Logo 在关于页和 Windows 任务栏中清晰且比例合适，页面内容继续保持左对齐。

本计划依据用户提供的三张运行截图制定。执行前需复现并检查当前代码，不能只通过调整截图或隐藏问题来满足验收。

## 2. 截图中确认的三个问题

### 问题一：设置卡片内容缺失，控件像是脱离了所属设置

通用页截图中，“启动”分组标题下面只看到两个彼此分开的 ToggleSwitch，没有显示对应的设置卡片、图标、标题和说明；“语言”下面也只看到一个按钮。主题下拉框同样像是独立控件。开关的归属和含义不清楚，页面出现不合理的大片垂直空白。

关于页截图中，“版本”“项目”等内容也主要剩下标题、按钮或链接，设置卡片的边框、图标、名称和说明没有完整呈现。由此需要检查所有复用 `SettingCard` 的页面，不能只修通用页的两个开关。

### 问题二：关于页品牌区未正确展示 Logo，且位置与页面左侧基线不一致

关于页截图中，品牌区只明显看到居中的 `ScreenLens` 名称和副标题，预期 Logo 没有清楚显示；品牌区横向位置相对页面标题明显向右，和页面统一左对齐的要求不一致。版本、项目、声明部分的层级和间距也需在卡片恢复后重新核对。

### 问题三：Windows 任务栏 Logo 的视觉尺寸明显偏小

任务栏截图中 ScreenLens 图标看起来远小于周围应用图标，内部图形没有充分利用图标可用区域。不能只把原 PNG 输出成不同像素尺寸；还需检查源图透明边距、图标资源使用的尺寸变体、Windows 图标安全区和最终运行时显示效果。

## 3. 优先级任务

### P0：恢复所有设置卡片的完整内容

- 检查 `Controls/SettingCard.xaml`、其代码后置、资源字典、内容属性绑定及每个页面的 XAML 用法，找出为何卡片容器或卡片自有内容没有显示，而内部按钮、下拉框和开关却单独出现。
- 修复根因，确保每个 `SettingCard` 同时显示图标、标题、描述、边框 / 背景和右侧操作控件；不能通过给孤立控件额外添加页级标题来掩盖卡片缺失。
- 按通用页逐项核对：主题模式、开机自动启动、启动后最小化、界面语言都必须有明确的标题和说明；第二个启动选项的禁用状态也要清楚可见，并与上一个选项保持一致的卡片结构。
- 按关于页核对：版本、项目、技术栈等信息应按统一卡片样式呈现；声明区域保留为清晰的说明容器。
- 扫描其余截图与选区、OCR 与结果、翻译、快捷键、外观页面，检查相同问题是否存在，逐页修复而不是只针对截图写特例。
- 控件需留在所属卡片的操作槽内；宽度不足时可换行或改成上下结构，不允许控件漂到卡片外、互相覆盖或挤压标题描述。

### P0：修正关于页品牌区布局与 Logo 显示

- 确认 `Assets/BrandLogo.png` 是从仓库根目录 `logo.png` 生成的有效资源，并确认构建输出中确实包含它；如果路径、打包方式或资源 URI 错误，修复真实引用问题。
- 关于页品牌区按页面内容左侧基线排列，不要居中悬浮；Logo、产品名、副标题形成一个紧凑且垂直对齐的品牌组。
- Logo 按正方形比例显示，建议在 48–64 DIP 范围内根据截图视觉校准；保持透明背景与清晰边缘，不裁掉图形，不拉伸变形。
- 品牌区与页面标题、副标题、版本卡片之间使用一致、克制的间距；恢复卡片后不能留下截图中那种内容散落、左右起点不齐或大块无意义空隙。

### P0：修正任务栏及应用图标的视觉比例

- 追踪 Windows 窗口图标和任务栏图标实际使用的资源链路，包括窗口图标设置、`Package.appxmanifest`、项目 `.csproj` 的资源声明和 `Assets/` 里的 scale / targetsize 文件；确认当前运行方式实际读取的是哪一份图标。
- 使用仓库根目录 `logo.png`（1254×1254）作为唯一品牌源图，源文件保持不变。检查源图 Alpha 透明边界和有效图形包围盒，区分“导出像素尺寸足够”与“图形在画布中视觉尺寸足够”。
- 从源图生成所需的应用图标变体。为任务栏 / 小尺寸图标合理减少多余透明边距，使图形在 Windows 图标安全区内尽可能清楚、饱满；不得裁切图形重要部分、改变比例或重绘品牌。
- 按需适配 Windows 的浅色 / 深色任务栏和透明背景显示规则；如果原图在某些背景下对比不足，先通过安全留白或符合 Windows 图标规范的背景承托处理，不要擅自改品牌主色。
- 检查窗口标题栏、任务栏、开始菜单 / 包图标等当前项目实际启用的入口，确保同一品牌资产体系一致。宽幅磁贴或启动图不应把方形 Logo 拉成长条；无关资源不必为了“全换”而破坏其布局。
- 以真实运行截图与周围常见任务栏图标作视觉对照，调整至整体视觉尺寸接近同排图标；不能仅以文件像素尺寸、构建成功或资源存在作为通过依据。

### P1：统一页面基础布局与细节

- 页面标题、副标题、分组标题、卡片左边缘和卡片间距建立一致的左侧基线；继续遵循用户偏好：设置内容左对齐，不做居中布局。
- 核对默认窗口、窄窗口和最大化窗口下卡片布局。标题、描述、下拉框、按钮和 ToggleSwitch 均完整可读，右侧控件不会溢出；必要时卡片从横向切换为纵向布局。
- 检查页面滚动到底部时最后一张卡片或声明说明不被窗口边缘裁切；页面间切换不应产生异常空白或内容跳动。
- 保持现有深色 / 浅色主题兼容；卡片边框、标题、次级描述、链接和禁用态对比度清楚，不能只在深色主题下可读。
- 保持现有通知浮层不推动页面内容的行为；如果实现仍存在，按上一阶段要求修复；如果已修复，则验证通知出现 / 消失时卡片位置稳定，不回退。

## 4. 本阶段边界

- 只改 WinUI 前端 XAML、C#、资源字典、图像资源与包清单中为修复这些 UI 问题所必需的部分。
- 不修改 Python 版、`screenlens/`、`tests/`、`run.py` 或后端接口。
- 不接入真实开机启动、持久化设置、托盘功能、截图、OCR、翻译 API 或网络请求；原型功能保持演示状态。
- 不替换用户提供的 `logo.png`，不改动其源文件内容，不新增大型 UI 框架或无关依赖。
- 不顺手重做整套视觉风格；优先修复卡片缺失、Logo 显示 / 比例和截图明确指出的问题，再处理与其直接相关的一致性细节。

## 5. 实际运行验收

使用 x64 构建并启动应用，逐项实际查看，不得只看 XAML：

1. **通用页**：主题、两个启动选项和语言均呈现完整卡片；标题、描述、图标与控件关系一目了然，禁用态正确。
2. **关于页**：Logo 可见且清晰；品牌区与页面左侧内容基线对齐；版本、项目、技术栈卡片和声明区完整、间距协调。
3. **其他五个设置页**：逐页确认所有 `SettingCard` 都同时显示卡片内容与操作控件，没有孤立按钮 / 开关、空卡片或错位内容。
4. **任务栏图标**：关闭并重新启动应用，检查实际窗口和任务栏图标。截图中图标应清晰、比例正确、视觉大小接近周围应用图标，且换主题或窗口状态后不发生异常。
5. **窗口尺寸**：在默认、窄窗口、最大化状态检查卡片的换行、滚动、控件可见性与页面左对齐。
6. **主题和通知**：至少检查深色和浅色下文字 / Logo / 卡片对比度；触发通知时确认内容位置保持稳定。
7. 保存至少四张实际运行截图：通用页、关于页、另一个设置页、任务栏图标。截图须能看清相关问题已经修复；不要用设计稿或静态资源预览替代运行截图。

## 6. 构建与交付

沿用项目要求的 x64 构建配置：

```powershell
dotnet build winui\ScreenLens.WinUI\ScreenLens.WinUI.csproj `
  --configuration Debug `
  --no-restore `
  --verbosity minimal `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64
```

完成后按三个截图问题逐项汇报：根因、修改文件、运行验证结果、截图位置，以及无法验证的系统级图标 / 窗口状态。不得只报告“已完成”或“构建成功”。如果运行截图未能证明任务栏尺寸正常，应明确标为未解决，继续修复后再交付。

## 7. 执行记录（2026-09-28 实施）

### 问题一：设置卡片内容缺失，控件像脱离所属设置

**根因**：`SettingCard` 是 `UserControl`。WinUI 把 UserControl 的 XAML 根元素（`Border` → `Grid` → 图标/标题/说明/内容槽）作为它的 `Content`；而页面里写成

```xml
<controls:SettingCard Icon="…" Title="…">
    <ComboBox … />
</controls:SettingCard>
```

时，`ComboBox` 又被 XAML 解析器赋给同一个 `Content` 属性，**直接把整张卡片的视觉树替换掉了**。结果只剩内部控件单独显示，边框、图标、标题、描述全部消失——与截图完全一致。

**修复**：给 `SettingCard` 加 `[ContentProperty(nameof(ActionContent))]` 并把 XAML 子元素重定向到新的 `ActionContent` 依赖属性，卡片内部的 `ContentPresenter` 绑定该属性。

- `Controls/SettingCard.xaml(.cs)` ✎：新增 `ActionContent` DP；`ContentPresenter` 由 `{Binding Content, ElementName=CardHost}` 改为 `{x:Bind ActionContent}`；加 `[ContentProperty]`。
- 七个设置页 + 快捷键页的 `DataTemplate` **用法零改动**（仍直接写子元素），全部一次性恢复正常。

**验证**：`docs/screenshots/` 下七个页面的实际运行截图（`…_General/_Capture/_Ocr/_Translate/_Hotkeys/_Appearance/_About`）均显示完整卡片：图标 + 标题 + 说明 + 右侧操作控件同行；「启动后最小化」禁用态清晰可见；不再有孤立开关或按钮。

### 问题二：关于页品牌区 Logo 未显示、位置与左侧基线不一致

**根因（两层）**：

1. **资源 URI 解析基准错误**。`Assets/BrandLogo.png` 是相对 URI，解析基准是**引用它的 XAML 文件所在目录**。`MainWindow.xaml` 在项目根，能解析到 `Assets/…`；而 `Views/Settings/AboutPage.xaml` 会解析成 `Views/Settings/Assets/BrandLogo.png` → 加载失败。图片虽加载失败但仍占宽 56 DIP，把产品名与副标题整体向右推，于是出现"品牌区明显右移"。
2. **Assets 未部署到应用目录**。构建只把资源写进 `bin\…\win-x64\AppX\Assets\`，exe 同级目录没有 `Assets\`；未打包运行时的 XAML 资源以应用目录为基准，即便 URI 正确也会加载失败。

**修复**：

- `Views/Settings/AboutPage.xaml` ✎、`MainWindow.xaml` ✎：改用绝对 URI `ms-appx:///Assets/BrandLogo.png`。
- `ScreenLens.WinUI.csproj` ✎：新增 `<Content Update="Assets\**" CopyToOutputDirectory="PreserveNewest" />`，让 Assets 同时部署到 exe 同级目录（构建后已确认 `win-x64\Assets\` 存在）。
- `AboutPage.xaml` ✎：品牌组按内容左侧基线排列（Logo 56 DIP 正方形，`Spacing=14`，`Margin="0,4,0,20"`），不再居中悬浮。

**验证**：`ScreenLens设置_About_phase7.png`（深色）与 `ScreenLens设置_About_light_phase7.png`（浅色）中 Logo 清晰可见，品牌组左边缘与「关于」页面标题、卡片左边缘在同一条基线上。

### 问题三：任务栏及应用图标的视觉尺寸明显偏小

**根因**：源图 `logo.png`（1254×1254）的实际图形只占画布约 69%（有效包围盒 871×863，起于 (192,195)），而原生成脚本在缩放后**又叠加了 8% 安全边距**，两层留白导致图形在图标内只有 ~64%。此外 `.csproj` 从未声明 `ApplicationIcon`，窗口/任务栏图标链路不完整。

**修复**：

- `scripts/generate_icons.py` ✎：先按 alpha 阈值裁掉透明边距并补成正方形（避免边缘极淡噪点让 `getbbox()` 形同虚设），再按用途分级留白——≤32px 任务栏/标题栏图标 0%、≤48px 2%、其余 4%；新增多尺寸 `ScreenLens.ico`（16/20/24/32/40/48/64/128/256，每个尺寸独立渲染）。
- `ScreenLens.WinUI.csproj` ✎：新增 `<ApplicationIcon>Assets\ScreenLens.ico</ApplicationIcon>`（exe 内嵌图标）并把 ico 纳入 Content。
- `MainWindow.xaml.cs` ✎：构造时 `AppWindow.SetIcon(<应用目录>\Assets\ScreenLens.ico)`，未打包运行时也能显式设置窗口/任务栏图标。

**验证**：`taskbar_apps_phase7.png`（运行中应用区放大 6 倍）中 ScreenLens 图标与相邻应用（ChatGPT、Discord、小米等）视觉尺寸相当且更饱满；图形在图标内的占比由约 64% 提升到 92%（32px 以上）／100%（≤32px）。后续为避免桌面媒体卡片信息进入仓库，已将 `taskbar_phase7.png` 替换为任务栏应用区的安全裁切图。

### P1：统一页面基础布局与细节

- **卡片宽度基线统一**：原 `SettingsPagePanel` 设了 `HorizontalAlignment="Left"`，StackPanel 宽度退化为"内容自然宽度"，各页卡片宽窄不一（实测同一窗口下 615 DIP vs 447 DIP）。改为：`MainWindow.xaml` 的内容 `Frame` 宽度绑定 `ScrollViewer.ViewportWidth` 并经新增的 `Converters/WidthClampConverter.cs` 钳制到 1000 DIP、左对齐；`SettingsPagePanel` 去掉 Left 对齐、撑满容器。修复后七页卡片左右边缘完全对齐。
- **窄窗口布局**：`SettingCard.xaml` ✎ 增加 `VisualStateManager` + `AdaptiveTrigger`，窗口逻辑宽 < 900 DIP 时操作控件换行到标题下方（避免与说明文字互相挤压），≥ 900 保持同行右对齐。窄窗口截图 `ScreenLens设置_General_narrow_phase7.png`（900×650）中标题、说明、开关均完整可读、无溢出。
- **高 DPI 下的默认窗口尺寸**：`AppWindow.Resize` 使用物理像素，150% 缩放下原「1180×760」实际只有 787×507 逻辑像素（连带把默认窗口挤进窄布局）。`MainWindow.xaml.cs` ✎ 新增 `ResizeToLogicalSize`，按 `GetDpiForWindow` 换算，使默认窗口在各种缩放下都是 1180×760 DIP。
- **主题与通知**：深色/浅色截图文字、Logo、卡片边框对比度均正常；顶部通知为覆盖层，出现时内容位置不动（浅色外观页截图正中可见该浮层与稳定布局）。

### 修改文件清单（均在 `winui/ScreenLens.WinUI/`）

| 文件 | 变更 |
|---|---|
| `Controls/SettingCard.xaml(.cs)` | ActionContent + ContentProperty；窄窗口自适应状态 |
| `Converters/WidthClampConverter.cs` | ✚ 宽度钳制转换器 |
| `MainWindow.xaml(.cs)` | ms-appx 绝对 URI；Frame 宽度钳制；显式设置窗口图标；逻辑像素 Resize |
| `Views/Settings/AboutPage.xaml` | ms-appx 绝对 URI；品牌组左侧基线排列 |
| `Styles/ThemeResources.xaml` | SettingsPagePanel 撑满容器（去掉 Left 对齐） |
| `ScreenLens.WinUI.csproj` | ApplicationIcon；Assets 部署到应用目录；注册新增资源 |
| `scripts/generate_icons.py` | 裁剪透明边距；分级安全留白；多尺寸 ico |
| `scripts/capture_winui_screenshots.ps1` | ✚ UI Automation 切页 + 置顶截图 + 窄/最大化 + 任务栏对照 |

### 验收截图（`docs/screenshots/`）

| 文件 | 内容 |
|---|---|
| `ScreenLens设置_General_phase7.png` | 通用页（深色，默认 1180×760）：卡片完整、等宽 |
| `ScreenLens设置_Capture_phase7.png` | 截图与选区页 |
| `ScreenLens设置_Ocr_phase7.png` | OCR 与结果页 |
| `ScreenLens设置_Translate_phase7.png` | 翻译页（含连接参数演示表单） |
| `ScreenLens设置_Hotkeys_phase7.png` | 快捷键页：DataTemplate 内卡片同样完整 |
| `ScreenLens设置_Appearance_phase7.png` | 外观页 |
| `ScreenLens设置_About_phase7.png` | 关于页：Logo 显示、品牌组左对齐 |
| `ScreenLens设置_General_narrow_phase7.png` | 窄窗口 900×650：操作控件换行、无溢出 |
| `ScreenLens设置_Translate_max_phase7.png` | 最大化：卡片等宽且内容左对齐 |
| `ScreenLens设置_Appearance_light_phase7.png` | 浅色主题 + 通知浮层不推动内容 |
| `ScreenLens设置_About_light_phase7.png` | 浅色主题下 Logo 与卡片对比度 |
| `taskbar_phase7.png` | ScreenLens 与相邻任务栏应用图标对照的安全裁切图，不含整条任务栏的媒体卡片和系统托盘信息 |

### 构建与运行验证

- `dotnet build winui\ScreenLens.WinUI\ScreenLens.WinUI.csproj -c Debug --no-restore --verbosity minimal -p:Platform=x64 -p:RuntimeIdentifier=win-x64`：0 警告 0 错误。
- 应用启动后常驻 193 秒以上，无崩溃日志（`%TEMP%\screenlens_winui_crash.log` 不存在）。
- Windows 环境实测：2560×1600 @150% 缩放；任务栏坐标为 UI Automation 报告的物理像素（2560×72）。

### 仍无法完全验证 / 仍为模拟状态

- **系统级图标入口**：开始菜单、任务栏固定（Pin）、Alt-Tab 缩略图、多显示器不同 DPI 下的图标由 Windows 自行选择资源档位，本机只能通过 exe 内嵌图标 + 窗口图标 + 实拍任务栏对照验证，无法逐一断言所有系统入口。
- **任务栏自动隐藏 / 深色浅色任务栏**：未逐一枚举系统任务栏主题设置组合。
- 原型功能保持演示状态：开机启动、设置持久化、托盘、真实截图/OCR/翻译均未接入（与本阶段边界一致）。
- 本阶段未改动 `screenlens/`、`tests/`、`run.py` 与后端接口；`logo.png` 源文件保持不变。

## 8. 后续界面细节修正（2026-09-29）

- 翻译服务参数改为统一的标签 / 输入框两列布局，输入控件左边缘一致，不再在卡片内横向居中悬浮。
- 固定左侧导航为展开状态并隐藏折叠按钮；标题栏隐藏重复的窗口 Logo，侧栏仍保留品牌 Logo，应用 / 任务栏图标保持不变。
- `启动后最小化` 卡片本身保持正常显示，只禁用开关；为深色 / 浅色主题提供可辨识的禁用轨道颜色。UI Automation 检查两个开关控件布局边界均为 78×60 物理像素。
- 任务栏截图脚本现在仅保存 ScreenLens 图标附近的应用区域，不再输出整条任务栏；旧的带媒体标题截图已由安全裁切版本替代。
- 本轮 x64 Debug 构建 0 警告、0 错误；运行实例响应正常，UI Automation 检查七个导航项均保持同一完整宽度，翻译标签与输入框处于固定列。
- 当前执行环境的 `Graphics.CopyFromScreen` 返回“句柄无效”，因此未能在这轮生成新的运行截图；浅色禁用轨道的最终视觉效果仍需在可截图的桌面会话中目视确认。截图脚本已补充浅色主题选项以便复验。
