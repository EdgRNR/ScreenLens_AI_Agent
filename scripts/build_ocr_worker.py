"""Build/publish the on-demand C# OCR worker and copy existing offline models.

    .venv\\Scripts\\python.exe scripts/build_ocr_worker.py

Output: dist/ocr/ScreenLens.Ocr.Worker.exe (net8, framework-dependent).
No model download, no inference or background process is started by this script.
"""
from __future__ import annotations

import argparse
import importlib.metadata
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from screenlens.agent.ocr_worker import MODEL_FILES


def package_dir(name: str) -> Path:
    spec = importlib.util.find_spec(name)
    if spec is None or spec.origin is None:
        raise RuntimeError(f"缺少 {name}，请先安装 requirements.txt")
    return Path(spec.origin).parent


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", type=Path, help="Optional NuGet cache directory")
    parser.add_argument("--no-restore", action="store_true")
    options = parser.parse_args()
    version = importlib.metadata.version("onnxruntime")
    if version != "1.30.0":
        raise RuntimeError(f"C# worker 需 onnxruntime==1.30.0，当前是 {version}")
    native = package_dir("onnxruntime") / "capi"
    models = package_dir("rapidocr") / "models"
    for path in [native / "onnxruntime.dll", native / "onnxruntime_providers_shared.dll",
                 *(models / name for name in MODEL_FILES)]:
        if not path.is_file():
            raise RuntimeError(f"缺少本地文件：{path}")
    project = ROOT / "ocr/ScreenLens.Ocr.Worker/ScreenLens.Ocr.Worker.csproj"
    output = ROOT / "dist/ocr"
    properties = [f"-p:OnnxRuntimeNativeDir={native}"]
    if options.packages:
        properties.append(f"-p:RestorePackagesPath={options.packages.resolve()}")
    if not options.no_restore:
        subprocess.run(["dotnet", "restore", str(project), *properties], cwd=ROOT, check=True)
    subprocess.run(["dotnet", "publish", str(project), "-c", "Release", "--no-restore",
                    "-o", str(output), *properties], cwd=ROOT, check=True)
    deployed_models = output / "models"
    deployed_models.mkdir(parents=True, exist_ok=True)
    for name in MODEL_FILES:
        shutil.copy2(models / name, deployed_models / name)
    notices = output / "licenses"
    notices.mkdir(parents=True, exist_ok=True)
    for name in ("LICENSE", "NOTICE.md"):
        shutil.copy2(project.parent / "RapidOCR" / name, notices / name)
    print(f"OCR worker ready: {output}")


if __name__ == "__main__":
    main()
