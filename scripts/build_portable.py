"""Build the current x64 WinUI/Agent application, then verify and zip it.

    .venv\\Scripts\\python.exe scripts/build_portable.py

Uses installed Python dependencies/models; never copies user configuration.
Every build gets a new directory. The legacy Tk build remains separate.
"""
from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import importlib.metadata
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from screenlens.agent.ocr_worker import MODEL_FILES


def package_dir(name):
    spec = importlib.util.find_spec(name)
    if not spec or not spec.origin:
        raise RuntimeError(f"Missing build dependency: {name}")
    return Path(spec.origin).parent


def run(command, log):
    print("Running:", subprocess.list2cmdline([str(part) for part in command]), flush=True)
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.run([str(part) for part in command], cwd=ROOT,
                                 stdout=output, stderr=subprocess.STDOUT,
                                 env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    if process.returncode:
        print(log.read_text(encoding="utf-8", errors="replace")[-14000:].encode(
            sys.stdout.encoding or "utf-8", errors="replace").decode(sys.stdout.encoding or "utf-8"))
        raise RuntimeError(f"Build failed ({process.returncode}); log: {log}")
    print(f"Done; log: {log}", flush=True)


def freeze(stage):
    spec = stage / "portable.spec"
    # Separate windowed Agent and console worker, sharing the onedir runtime.
    # CREATE_NO_WINDOW hides the worker while preserving framed stdin/stdout.
    excluded = ["tkinter", "_tkinter", "rapidocr", "onnxruntime", "numpy", "cv2",
                "screenlens.app", "screenlens.ui", "screenlens.ocr", "mss"]
    common = f"pathex=[{str(ROOT)!r}], excludes={excluded!r}, noarchive=False"
    spec.write_text(f'''
agent = Analysis([{str(ROOT / "run_agent.py")!r}],
    datas=[({str(ROOT / "logo.png")!r}, ".")],
    hiddenimports=["pystray._win32"], {common})
worker = Analysis([{str(ROOT / "run_worker.py")!r}],
    datas=[], hiddenimports=[], {common})
agent_pyz = PYZ(agent.pure)
worker_pyz = PYZ(worker.pure)
agent_exe = EXE(agent_pyz, agent.scripts, [], exclude_binaries=True,
    name="ScreenLensAgent", console=False, icon={str(ROOT / "winui/ScreenLens.WinUI/Assets/ScreenLens.ico")!r})
worker_exe = EXE(worker_pyz, worker.scripts, [], exclude_binaries=True,
    name="ScreenLensWorker", console=True, icon={str(ROOT / "winui/ScreenLens.WinUI/Assets/ScreenLens.ico")!r})
bundle = COLLECT(agent_exe, worker_exe, agent.binaries, agent.datas,
    worker.binaries, worker.datas, name="python-bundle")
''', encoding="utf-8")
    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--distpath", stage / "frozen",
         "--workpath", stage / "freeze-work", spec], stage / "freeze.log")
    return stage / "frozen/python-bundle"


def add_notices(output, native):
    licenses = output / "licenses"
    licenses.mkdir(exist_ok=True)
    versions = {}
    for name in ("pystray", "keyboard", "Pillow", "requests", "certifi", "urllib3",
                 "charset-normalizer", "idna", "rapidocr", "onnxruntime", "pyinstaller"):
        distribution = importlib.metadata.distribution(name)
        versions[name] = distribution.version
        for relative in distribution.files or []:
            lower = str(relative).lower()
            if (".dist-info/" in lower or ".egg-info/" in lower) and any(
                    word in Path(lower).name for word in ("license", "copying", "notice", "authors")):
                source = Path(distribution.locate_file(relative))
                if source.is_file():
                    target = licenses / name / Path(str(relative)).name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)
    python_license = Path(sys.base_prefix) / "LICENSE.txt"
    if python_license.is_file():
        shutil.copy2(python_license, licenses / "Python-LICENSE.txt")
    for name in ("LICENSE", "NOTICE.md"):
        shutil.copy2(ROOT / "ocr/ScreenLens.Ocr.Worker/RapidOCR" / name, licenses / f"RapidOCR-{name}")
    # Native ONNX Runtime distribution includes its notices beside the DLLs.
    for name in ("LICENSE", "ThirdPartyNotices.txt"):
        for source in (native / name, native.parent / name):
            if source.is_file():
                shutil.copy2(source, licenses / f"ONNXRuntime-{name}")
                break
    # Keep the licenses shipped by the selected native NuGet packages.
    cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    runtime = json.loads((output / "ScreenLens.WinUI.runtimeconfig.json").read_text(encoding="utf-8"))
    dotnet_version = next(f["version"] for f in runtime["runtimeOptions"]["includedFrameworks"]
                          if f["name"] == "Microsoft.NETCore.App")
    for name, version in (("emgu.cv", "4.12.0.5764"), ("emgu.cv.runtime.windows", "4.12.0.5764"),
                          ("clipper2", "2.0.0"), ("microsoft.ml.onnxruntime.managed", "1.30.0"),
                          ("microsoft.windowsappsdk", "2.5.1"),
                          ("microsoft.windowsappsdk.winui", "2.3.9"),
                          ("microsoft.netcore.app.runtime.win-x64", dotnet_version)):
        folder = cache / name / version
        versions[name] = version
        if folder.is_dir():
            for source in folder.rglob("*"):
                if source.is_file() and any(word in source.name.lower() for word in ("license", "notice")):
                    target = licenses / name / source.relative_to(folder)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)
    (licenses / "DEPENDENCIES.json").write_text(json.dumps(versions, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", default="ScreenLens-portable-win-x64-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    parser.add_argument("--no-restore", action="store_true", help="Use previously restored RID assets")
    args = parser.parse_args()
    if Path(args.name).name != args.name or args.name in (".", ".."):
        parser.error("name must be a directory name, not a path")
    output = ROOT / "dist" / args.name
    stage = ROOT / "build" / args.name
    archive = output.with_suffix(".zip")
    if output.exists() or stage.exists() or archive.exists():
        raise RuntimeError("Build destination already exists; choose a new --name")
    if importlib.metadata.version("onnxruntime") != "1.30.0":
        raise RuntimeError("The OCR worker requires onnxruntime==1.30.0")
    package_dir("PyInstaller")
    native = package_dir("onnxruntime") / "capi"
    models = package_dir("rapidocr") / "models"
    for source in [native / "onnxruntime.dll", native / "onnxruntime_providers_shared.dll",
                   *(models / name for name in MODEL_FILES)]:
        if not source.is_file():
            raise RuntimeError(f"Missing local OCR dependency: {source}")
    stage.mkdir(parents=True)
    projects = [
        (ROOT / "winui/ScreenLens.WinUI/ScreenLens.WinUI.csproj", stage / "frontend",
         ["-p:Platform=x64", "-p:WindowsAppSDKSelfContained=true"]),
        (ROOT / "ocr/ScreenLens.Ocr.Worker/ScreenLens.Ocr.Worker.csproj", stage / "ocr",
         [f"-p:OnnxRuntimeNativeDir={native}"]),
    ]
    for project, target, properties in projects:
        command = ["dotnet", "publish", project, "-c", "Release", "-r", "win-x64",
                   "--self-contained", "true", "-p:PublishTrimmed=false", "-p:PublishSingleFile=false",
                   "-p:PublishReadyToRun=false", "-p:DebugType=None", "-o", target, *properties]
        if args.no_restore:
            command.append("--no-restore")
        run(command, stage / f"{target.name}.log")
    frozen = freeze(stage)
    shutil.copytree(stage / "frontend", output)
    shutil.copytree(frozen, output, dirs_exist_ok=True)
    shutil.copytree(stage / "ocr", output / "ocr")
    (output / "ocr/models").mkdir()
    for name in MODEL_FILES:
        shutil.copy2(models / name, output / "ocr/models" / name)
    # App-local VC runtime files from the redistributed Emgu native package,
    # so Python/WinUI/OCR do not require a separately installed VC runtime.
    for source in (stage / "ocr").rglob("*140*.dll"):
        if "win-arm64" not in str(source) and "win-x86" not in str(source):
            for target in (output, output / "_internal", output / "ocr"):
                shutil.copy2(source, target / source.name)
    add_notices(output, native)
    (output / "使用说明.txt").write_text(
        "ScreenLens Windows x64 免安装测试包\n\n"
        "1. 将整个文件夹解压到固定目录，不要直接在 ZIP 内运行或只复制 exe。\n"
        "2. 双击 ScreenLens.WinUI.exe 打开设置，后台托盘会自动启动。\n"
        "   只想启动托盘时，双击 ScreenLensAgent.exe。默认截图快捷键为 Ctrl + `。\n"
        "3. 测试前请退出开发版 Run Agent 和 ScreenLens 窗口，避免连接旧进程。\n"
        "4. 无需安装 Python、.NET 或 Windows App SDK；识别模型已离线携带。\n"
        "5. 配置仍保存在用户的 AppData/ScreenLens，下次运行会保留设置。\n"
        "   本包与开发版共用该配置，压缩包不包含你的配置、API 密钥或截图。\n"
        "6. 复制截图、保存、识别、翻译、重新截图和多屏显示请实际测试。\n"
        "   OCR 按需启动，空闲 30 秒回收，不提前预热。\n"
        "7. 开启自启动后不要随意移动目录；移动后关闭再开启自启动更新路径。\n"
        "   删除测试包前先关闭自启动并从托盘退出。\n"
        "8. 翻译需要联网；API 参数在设置中填写。第三方说明在 licenses 文件夹。\n"
        "\n本包是测试版；干净 Windows 设备与真实登录启动尚需实测。\n",
        encoding="utf-8-sig")
    ref = subprocess.check_output(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, text=True).strip()
    (output / "BUILD.json").write_text(json.dumps({"built_at": datetime.now().isoformat(),
        "base_commit": ref, "includes_uncommitted_packaging_changes": True,
        "platform": "win-x64", "self_contained": True, "python": sys.version.split()[0]}, indent=2), encoding="utf-8")
    run([sys.executable, ROOT / "scripts/check_portable.py", output, "--work-dir", stage / "validation"],
        stage / "validation.log")
    # A ZIP with one enclosing directory prevents DLL/asset files from being
    # scattered across the user's Downloads folder.
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zip_output:
        for source in sorted(output.rglob("*")):
            if source.is_file():
                zip_output.write(source, Path(output.name) / source.relative_to(output))
    with zipfile.ZipFile(archive) as zip_output:
        damaged = zip_output.testzip()
        if damaged:
            raise RuntimeError(f"ZIP CRC failed: {damaged}")
    with archive.open("rb") as archive_input:
        digest = hashlib.file_digest(archive_input, "sha256").hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    print(f"Portable package ready: {archive}\nSize: {archive.stat().st_size / 1024**2:.1f} MiB\nSHA256: {digest}", flush=True)


if __name__ == "__main__":
    main()
