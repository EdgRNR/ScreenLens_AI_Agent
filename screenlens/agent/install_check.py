"""Read-only bundle diagnostics: no tray, hooks, configuration or OCR preload."""
import json
from pathlib import Path
import sys

from screenlens.agent.frontend import find_frontend_exe
from screenlens.agent.ocr_worker import native_ocr_command
from screenlens.agent.startup import startup_command
from screenlens.agent.workers import WorkerManager
from screenlens.tray import _make_icon_image


def check_install(output: str) -> int:
    report = {"frozen": bool(getattr(sys, "frozen", False)), "executable": sys.executable}
    try:
        frontend = find_frontend_exe()
        native = native_ocr_command(str(Path(__file__).resolve().parents[2]))
        if not frontend or not native:
            raise FileNotFoundError("前端或 OCR worker / 模型不完整")
        report.update(frontend=frontend, ocr_command=native,
                      translation_command=WorkerManager._translation_worker_command(),
                      startup_command=startup_command())
        with _make_icon_image() as icon:
            report["tray_icon_size"] = list(icon.size)
        report["ok"] = True
    except Exception as error:
        report.update(ok=False, error=str(error))
    Path(output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if report["ok"] else 1
