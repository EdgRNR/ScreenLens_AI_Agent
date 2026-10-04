# 截图首次显示白闪修复

原流程在 `PrepareForDisplayAsync()` 加载位图后立即 `Activate()`。
位图赋值并不等于 WinUI 布局、绘制及桌面合成已经完成，首次显示会暴露
冻结截图尚未覆盖的窗口表面。多屏采样捕捉到原本较暗的区域变为纯白，
发生在首次激活期间、冻结背景绘制完成之前。

`SelectionWindow.ShowForCaptureAsync()` 在首次激活前使用 `DWMWA_CLOAK`
遮蔽整个 HWND 的桌面呈现，让 WinUI 在不可见期间完成布局和绘制。
收到两次尺寸有效的 Rendering 回调后，再等待 `RequestCommitAsync()`
以及 `DwmFlush()`，最后解除遮蔽并前置窗口。Rendering 回调本身不是
物理显示的证明；组合等待避免直接把未准备的窗口表面呈现出来。

重复快捷键和 Loaded 前置逻辑不能绕过首次准备。关闭窗口会中断等待；
三秒超时仅用于失败处理，不是每次截图的固定延迟。结果窗口的重新截图
使用相同流程。Esc 的先隐藏再关闭逻辑、热启动转发保持原有实现。

用户已通过 VS Code Run Agent 和截图快捷键，在主、副屏实际确认白闪消失。
临时颜色探针、桌面采样、原生背景刷试验及相关测试已从提交内容中移除，
本地历史诊断日志保留在被忽略的 `.local/capture-diagnostics/` 中。
具体哪个 WinUI/DWM 内部函数填出了白色尚未直接确认。

原生接口检查（Windows / .NET 8，不显示测试窗口或读取桌面）：

```powershell
dotnet run --project tests/winui/CaptureWindowPresentation.Check/CaptureWindowPresentation.Check.csproj
```

该检查验证隐藏 HWND 的遮蔽、同步解除遮蔽、可见性和重复调用，不能替代
WinUI 首帧、多屏选区及 Esc 取消的实际视觉验收。
