# Windows x64 免安装测试包

本包部署当前 WinUI 3 前端、Python Agent、独立 Python 翻译 worker 和 C#
OCR worker，采用同目录部署，不是旧 Tk 原型。仅支持 Windows x64。

## 使用

完整解压 ZIP 到固定目录，退出开发版 Run Agent 和已打开的 ScreenLens
窗口后，双击 `ScreenLens.WinUI.exe` 打开设置并自动启动后台托盘。
只需要托盘时运行 `ScreenLensAgent.exe`。默认截图键为 Ctrl + 反引号。

Python、.NET、Windows App SDK、原生依赖和三个 OCR 模型均在包内。
OCR 按需启动，空闲 30 秒回收；翻译需要网络和相应服务配置。

配置仍在 `%APPDATA%\ScreenLens\config.json` 和
`%LOCALAPPDATA%\ScreenLens\frontend.json`，与开发版共用，不在 ZIP 内。
开机启动记录的是当前 Agent 的绝对路径：移动文件夹后关闭再开启自启动；
删除测试包前先关闭自启动并从托盘退出。日志位于
`%LOCALAPPDATA%\ScreenLens\logs`。

## 构建

需要 Windows x64、.NET 8 SDK、项目 Python 环境及本地离线模型。
先安装 `requirements.txt` 和 PyInstaller；ONNX Runtime 必须为 1.30.0。

```powershell
.venv\Scripts\python.exe -m pip install pyinstaller
.venv\Scripts\python.exe scripts/build_portable.py
```

每次生成新的 `build/ScreenLens-portable-win-x64-时间戳/` 和
`dist/ScreenLens-portable-win-x64-时间戳/`，不会覆盖已有包。
可用 `--name` 指定单个目录名，`--no-restore` 使用已还原的 NuGet 依赖。
构建日志分别为 `frontend.log`、`ocr.log`、`freeze.log` 和 `validation.log`。
验证通过后才生成 ZIP 和 `.zip.sha256`；失败时保留目录便于排查。

前端与 OCR 使用 Release/self-contained，关闭裁剪以保留动态类型，
显式发布编译后的 XBF 页面和合并后的 `resources.pri` 主题资源索引。
设置侧栏和卡片图标使用内置矢量路径，随 XAML 资源发布，避免系统符号
字体在不同 Windows 版本上产生外观差异。
两个 PyInstaller 可执行文件共享 `_internal`
运行库：Agent 无控制台，翻译 worker 保留管道并以隐藏控制台方式启动。
便携包使用同目录 C# OCR，不提供 Python OCR 回退。第三方说明在 `licenses/`。

## 自动验证及边界

构建脚本复制包到含中文和空格的新目录，子进程不使用开发 Python/.NET
路径，隔离用户配置，并验证：

- 冻结 Agent 的前端、OCR、翻译 worker 路径和托盘 logo。
- WinUI 运行库与设置页 XAML 加载（无窗口，不连接用户 Agent）。
- 真实离线 OCR、重复调用、坏图后恢复。
- 独立翻译 worker 的模型列表、流式响应、连接测试和错误帧。
  在线接口以本地测试服务器替代，不使用真实 API Key。
- ZIP CRC 和 SHA-256。

可对已有包独立运行验证；指定新的空验证目录：

```powershell
.venv\Scripts\python.exe scripts/check_portable.py dist\包目录 --work-dir build\新验证目录
```

这不等同于干净 Windows 设备上的完整验收。真实桌面上的快捷键、多屏
截图、剪贴板、保存、重新截图、在线翻译和登录自启动仍需实测。
安装诊断不打开桌面窗口、不注册热键、不改启动注册表或用户配置。
