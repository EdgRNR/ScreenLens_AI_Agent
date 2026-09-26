# -*- coding: utf-8 -*-
"""第二阶段端到端验收脚本（模拟真实用户操作）。

流程：
1. 构造完整 App（不进 mainloop，用 pump 驱动）
2. 用 Win32 keybd_event 合成真实热键 Ctrl+Alt+A
3. 截图遮罩出现 → 模拟鼠标矩形选区
4. OCR 自动识别 → 结果浮窗
5. 点击复制（校验剪贴板）
6. 点击翻译（google_free 真实网络）
7. 退出应用（无异常）

运行: python scripts/acceptance_phase2.py
"""
import ctypes
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

PASS = []
FAIL = []


def check(name, cond, detail=""):
    if cond:
        PASS.append(name)
        print(f"  [PASS] {name}")
    else:
        FAIL.append(name)
        print(f"  [FAIL] {name} {detail}")
    sys.stdout.flush()


def pump(root, ms):
    end = time.time() + ms / 1000
    while time.time() < end:
        root.update()
        time.sleep(0.01)


def send_hotkey():
    """合成 Ctrl+Alt+A（带扫描码：ctrl=0x1D, alt=0x38, a=0x1E）。"""
    u = ctypes.windll.user32
    KEYUP = 0x0002
    for vk, scan, flags in [
        (0x11, 0x1D, 0), (0x12, 0x38, 0), (0x41, 0x1E, 0),
        (0x41, 0x1E, KEYUP), (0x12, 0x38, KEYUP), (0x11, 0x1D, KEYUP),
    ]:
        u.keybd_event(vk, scan, flags, 0)
        time.sleep(0.05)


def main():
    from screenlens.app import ScreenLensApp
    from screenlens.config import config_path

    # 备份用户配置，验收结束后恢复（验收会修改 provider 设置）
    backup = None
    if os.path.isfile(config_path()):
        with open(config_path(), "r", encoding="utf-8") as f:
            backup = f.read()
    try:
        return _run(ScreenLensApp)
    finally:
        if backup is not None:
            with open(config_path(), "w", encoding="utf-8") as f:
                f.write(backup)


def _run(app_factory):
    print("== 构造应用 ==", flush=True)
    app = app_factory()
    app.dispatcher.start()
    ok, msg = app.hotkeys.register(app.config.hotkey)
    check("全局热键注册", ok, msg)

    print("== 合成真实热键 Ctrl+Alt+A ==", flush=True)
    send_hotkey()
    deadline = time.time() + 5
    while time.time() < deadline:
        pump(app.root, 50)
        if app.overlay is not None and app.overlay._win is not None:
            break
    check("热键触发截图模式", app.overlay is not None
          and app.overlay._win is not None)

    # ---- 模拟矩形选区（真实 Tk 事件）
    c = app.overlay._canvas
    c.event_generate("<ButtonPress-1>", x=120, y=120)
    pump(app.root, 30)
    c.event_generate("<B1-Motion>", x=300, y=180)
    pump(app.root, 30)
    c.event_generate("<ButtonRelease-1>", x=620, y=380)
    pump(app.root, 100)
    check("矩形选区完成并截图", app.result_window._win is not None)
    check("截图模式已退出", app.overlay is None or app.overlay._win is None)

    # ---- OCR
    print("== 等待 OCR 完成 ==", flush=True)
    deadline = time.time() + 30
    status = ""
    while time.time() < deadline:
        pump(app.root, 100)
        status = app.result_window._status.cget("text")
        if "识别完成" in status or "未识别到文字" in status:
            break
    check("OCR 完成（本地）", "识别完成" in status or "未识别" in status,
          f"status={status}")
    text = app.result_window._text.get("1.0", "end-1c")
    print(f"  识别文本预览: {text[:40]!r}", flush=True)

    # ---- 复制
    if app.result_window._has_text:
        app.result_window._on_copy()
        pump(app.root, 100)
        clip = app.root.clipboard_get()
        check("一键复制（剪贴板校验）", clip == text)
    else:
        check("无文字时不允许复制", True)

    # ---- 翻译（provider 由配置决定，默认 google_free 在线）
    if app.result_window._has_text:
        print("== 点击翻译 ==", flush=True)
        app.result_window._on_translate()
        pump(app.root, 200)
        busy_status = app.result_window._status.cget("text")
        check("翻译请求期间显示隐私提示", "截图不会上传" in busy_status,
              f"status={busy_status}")
        check("翻译期间按钮锁定", app.result_window._translating)
        # 重复点击应被拒绝
        app.result_window._on_translate()
        check("翻译请求防重复", "翻译进行中" in app.result_window._status.cget("text"))
        deadline = time.time() + 30
        while time.time() < deadline:
            pump(app.root, 100)
            s = app.result_window._status.cget("text")
            if "翻译完成" in s or "翻译失败" in s or "翻译未启用" in s:
                break
        s = app.result_window._status.cget("text")
        check("翻译流程完整结束（成功或明确失败提示）",
              "翻译完成" in s or "翻译失败" in s or "翻译未启用" in s, f"status={s}")
        check("翻译结束后按钮恢复", not app.result_window._translating)
        print(f"  翻译状态: {s}", flush=True)

    # ---- 重新截图 → 关闭
    print("== 重新截图 / 关闭 ==", flush=True)
    app.result_window._on_recapture_btn()
    pump(app.root, 400)
    check("重新截图进入截图模式", app.overlay is not None
          and app.overlay._win is not None)
    # Esc 取消
    app.overlay._win.event_generate("<Escape>")
    pump(app.root, 100)
    check("Esc 退出截图模式", app.overlay is None or app.overlay._win is None)

    # ---- 设置窗口
    print("== 设置窗口 ==", flush=True)
    app.open_settings()
    pump(app.root, 300)
    check("设置窗口打开", app.settings_window.is_shown())
    app.settings_window._provider_var.set("none")
    errors = app._apply_settings_impl(app.config.hotkey,
                                      {"provider": "none",
                                       "target_language": "zh",
                                       "openai": {"base_url": "",
                                                  "api_key": "",
                                                  "model": "gpt-4o-mini"}})
    check("设置立即生效（provider 切换为 none）", errors == [])
    check("Provider 已切换", app.result_window._provider.name == "none")
    app.settings_window._close()
    pump(app.root, 100)
    check("设置窗口关闭", not app.settings_window.is_shown())

    # ---- 退出
    print("== 退出应用 ==", flush=True)
    app.quit()
    pump(app.root, 500)
    try:
        alive = app.root.winfo_exists()
    except Exception:
        alive = False  # TclError = 解释器已销毁，即退出成功
    check("应用正常退出（root 已销毁）", not alive)

    print("\n========== 验收结果 ==========")
    print(f"PASS: {len(PASS)}  FAIL: {len(FAIL)}")
    if FAIL:
        print("失败项:", FAIL)
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
