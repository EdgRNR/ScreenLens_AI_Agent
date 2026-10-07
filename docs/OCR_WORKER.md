# 按需 C# OCR worker

Agent 优先使用已构建的 C# OCR worker；翻译、流式响应和模型列表仍由
Python worker 处理。任务串行执行，切换 worker 类型时先释放前一个进程，
只保留一份 worker。按需启动，不预热，任务结束后空闲 30 秒退出。

## 构建与运行

需要 x64 .NET 8 SDK（运行时与前端一致），并先安装 `requirements.txt`。
项目将 ONNX Runtime 固定为 1.30.0，避免 C# managed API 和 Python 提供的
原生 DLL 版本不一致。使用现有的三个离线模型，不下载或更换模型。

```powershell
.venv\Scripts\python.exe scripts/build_ocr_worker.py
.venv\Scripts\python.exe run_agent.py
```

输出位于 `dist/ocr/`，包含 worker、DLL 和 `models/`。重启已经运行的
Run Agent 后即可生效，前端无需重新构建。构建脚本不启动识别进程。

开发环境查找顺序：`dist/ocr/`，其次 `ocr/ScreenLens.Ocr.Worker/bin/Release/net8.0/`。
模型优先从 worker 旁的 `models/` 读取，开发输出没有模型时读取当前
Python 环境的 RapidOCR 模型目录。找不到完整 worker/模型时自动使用
原 Python OCR，普通用户无需更改配置。

## 对照与回退

仅对当前 Agent 进程有效，不写入用户设置：

```powershell
$env:SCREENLENS_OCR_BACKEND = 'python' # 强制原 Python OCR
.venv\Scripts\python.exe run_agent.py
```

`auto`（默认）优先 C#，`csharp` 强制 C# 并在缺少构建产物时返回明确错误。
Agent 日志的 `worker 已启动 backend=... pid=...` 可确认实际使用的实现。

```powershell
.venv\Scripts\python.exe -m pytest tests/test_native_ocr_worker.py tests/test_agent_cancellation.py tests/test_translation_api.py
```

测试覆盖真实模型识别、大小图/空图、坏图后复用、冷启动中取消并重建、
OCR 与流式翻译进程切换、超限及截断协议帧。缺少 native 构建产物时，
真实 native 测试会跳过，路由单元测试仍运行。

## 发布与边界

worker 是 framework-dependent 的 x64 net8 控制台程序，启动时隐藏控制台。
当前发布包尚未统一制作；将 `dist/ocr/` 完整放在 Agent 程序旁的 `ocr/`
目录，确保目标设备有 x64 .NET 8 运行时。旧 `scripts/build_exe.py` 打包
的是 Tk 原型，不是当前 WinUI/Agent 发行版。

OCR 输出保持原 IPC 的 `text`、`lines[{text,score,box}]`、`elapsed_ms`，
检测框映射回原图尺寸。极端细长图片会将短边至少调整到 32 像素，避免
原 Python 管线在取整到 0 时失败。几种合成图的对照不能证明所有真实
截图的准确率完全相同，接入后仍需要日常截图验收。

第三方改编源码及来源位于 `ocr/ScreenLens.Ocr.Worker/RapidOCR/`。
