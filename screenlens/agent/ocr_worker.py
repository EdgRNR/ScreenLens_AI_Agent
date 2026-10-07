"""Resolve the optional native OCR worker without importing inference libraries."""
from __future__ import annotations

import importlib.util
import os
from pathlib import Path
import sys

MODEL_FILES = (
    "PP-OCRv6_det_small.onnx",
    "PP-OCRv6_rec_small.onnx",
    "ch_ppocr_mobile_v2.0_cls_mobile.onnx",
)
WORKER_FILES = (
    "ScreenLens.Ocr.Worker.exe", "ScreenLens.Ocr.Worker.dll",
    "ScreenLens.Ocr.Worker.runtimeconfig.json", "ScreenLens.Ocr.Worker.deps.json",
    "onnxruntime.dll", "onnxruntime_providers_shared.dll",
)


def native_ocr_command(repo_root: str) -> list[str] | None:
    """Packaged worker first, then local Release build; no model downloads."""
    if os.environ.get("SCREENLENS_OCR_BACKEND", "auto").lower() == "python":
        return None
    root = Path(repo_root)
    if getattr(sys, "frozen", False):
        folders = [Path(sys.executable).parent / "ocr"]
    else:
        folders = [root / "dist" / "ocr", root / "ocr" / "ScreenLens.Ocr.Worker" /
                   "bin" / "Release" / "net8.0"]
    for folder in folders:
        exe = folder / "ScreenLens.Ocr.Worker.exe"
        if not all((folder / name).is_file() for name in WORKER_FILES):
            continue
        models = folder / "models"
        if not models.is_dir() and not getattr(sys, "frozen", False):
            spec = importlib.util.find_spec("rapidocr")
            if spec is not None and spec.origin:
                models = Path(spec.origin).parent / "models"
        if all((models / name).is_file() for name in MODEL_FILES):
            return [str(exe), str(models)]
    return None
