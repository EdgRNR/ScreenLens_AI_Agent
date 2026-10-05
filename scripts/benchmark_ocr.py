"""Compare local OCR profiles on labelled synthetic screen text, without uploads.

Each profile runs in a fresh process. Reports contain actual recognised text,
character edit errors (both with and without whitespace), cold/warm timings,
and sampled worker private bytes. Synthetic results are not a real-world
accuracy guarantee. Requires the project's development dependency psutil.

Example: python scripts/benchmark_ocr.py --profiles baseline production
Models must already exist in RapidOCR's models directory, or --model-dir.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
DEFAULT_OUTPUT = ROOT / ".local/capture-diagnostics/ocr-benchmark"


def edit_distance(a, b):
    row = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        next_row = [i]
        for j, y in enumerate(b, 1):
            next_row.append(min(next_row[-1] + 1, row[j] + 1,
                                row[j - 1] + (x != y)))
        row = next_row
    return row[-1]


def make_cases():
    from PIL import Image, ImageDraw, ImageFont

    texts = {
        "zh": "截图识别：今天天气不错，我们一起去公园散步吧！",
        "mixed": "ScreenLens 中英文 mixed 第123行 Hello world 2026-10-04",
        "numbers": "订单编号 AB01OIl 0123456789 金额 ￥128.50 / 3.14159",
        "ja": "こんにちは、世界。日本語のテキスト認識テスト。",
    }
    cases = []
    for size in (12, 16, 24):
        for dark in (False, True):
            bg, fg = ((30, 30, 30), (220, 220, 220)) if dark else ("white", "black")
            for language, text in texts.items():
                font_name = "YuGothM.ttc" if language == "ja" else "msyh.ttc"
                font = ImageFont.truetype(str(Path(os.environ["WINDIR"]) / "Fonts" / font_name), size)
                width = int(font.getlength(text)) + 24
                image = Image.new("RGB", (width, size + 24), bg)
                ImageDraw.Draw(image).text((12, 8), text, font=font, fill=fg)
                cases.append((f"{language}-{size}-{'dark' if dark else 'light'}", image, text))
    font = ImageFont.truetype(str(Path(os.environ["WINDIR"]) / "Fonts/msyh.ttc"), 16)
    multiline = list(texts.values())[:3]
    image = Image.new("RGB", (700, 132), "white")
    draw = ImageDraw.Draw(image)
    for i, text in enumerate(multiline):
        draw.text((12, 8 + i * 36), text, font=font, fill="black")
    cases.append(("mixed-multiline", image, "\n".join(multiline)))
    for language in ("zh", "ja"):
        _, image, text = next(c for c in cases if c[0] == f"{language}-16-light")
        cases.append((f"{language}-upside-down", image.rotate(180), text))
    font = ImageFont.truetype(str(Path(os.environ["WINDIR"]) / "Fonts/msyh.ttc"), 10)
    text = texts["mixed"]
    image = Image.new("RGB", (int(font.getlength(text)) + 24, 36), (40, 40, 40))
    ImageDraw.Draw(image).text((12, 8), text, font=font, fill=(145, 145, 145))
    cases.append(("mixed-10-low-contrast", image, text))
    font = ImageFont.truetype(str(Path(os.environ["WINDIR"]) / "Fonts/msyh.ttc"), 16)
    text = texts["zh"]
    image = Image.new("RGB", (700, 90), "white")
    ImageDraw.Draw(image).text((12, 32), text, font=font, fill="black")
    cases.append(("zh-tilted", image.rotate(7, expand=True, fillcolor="white"), text))
    image = Image.new("RGB", (1600, 900), "white")
    draw = ImageDraw.Draw(image)
    full_lines = [texts["zh"] if i % 2 == 0 else texts["mixed"] for i in range(20)]
    for i, text in enumerate(full_lines):
        draw.text((60, 40 + i * 36), text, font=font, fill="black")
    cases.append(("screen-multiline-1600", image, "\n".join(full_lines)))
    cases.append(("blank", Image.new("RGB", (200, 100), "white"), ""))
    return cases


def params_for(profile, model_dir):
    if profile == "production":
        from screenlens.ocr.engine import _rapidocr_params

        params = _rapidocr_params()
        if model_dir:
            params["Global.model_root_dir"] = str(model_dir)
        return params
    from rapidocr import EngineType, LangCls, ModelType, OCRVersion

    params = {
        "Det.engine_type": EngineType.ONNXRUNTIME,
        "Rec.engine_type": EngineType.ONNXRUNTIME,
        "Cls.engine_type": EngineType.ONNXRUNTIME,
        "Det.ocr_version": OCRVersion.PPOCRV6,
        "Rec.ocr_version": OCRVersion.PPOCRV6,
        "Det.model_type": ModelType.SMALL,
        "Rec.model_type": ModelType.SMALL,
        "Det.lang_type": "ch",
        "Rec.lang_type": "ch",
        "Cls.ocr_version": OCRVersion.PPOCRV4,
        "Cls.model_type": ModelType.MOBILE,
        "Cls.lang_type": LangCls.CH,
    }
    screen = profile.startswith("screen-")
    profile = profile.removeprefix("screen-")
    if screen:
        params["Det.limit_type"] = "max"
    if profile.startswith("balanced"):
        params["Cls.cls_thresh"] = 0.99
    if profile.startswith("rec-medium") or profile.startswith("medium"):
        params["Rec.model_type"] = ModelType.MEDIUM
    if profile.startswith("medium"):
        params["Det.model_type"] = ModelType.MEDIUM
    if "-" in profile:
        params["EngineConfig.onnxruntime.intra_op_num_threads"] = int(profile.rsplit("-", 1)[1])
        params["EngineConfig.onnxruntime.inter_op_num_threads"] = 1
    if model_dir:
        params["Global.model_root_dir"] = str(model_dir)
    return params


def child_run(args):
    import psutil

    process = psutil.Process()
    samples = []
    stopped = threading.Event()

    def sample():
        while not stopped.is_set():
            info = process.memory_info()
            samples.append(info.vms if os.name == "nt" else info.rss)
            stopped.wait(0.02)

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    try:
        cases = make_cases()
        for name, image, _ in cases:
            image.save(args.output / f"{name}.png")
        cold_start = time.perf_counter()
        from rapidocr import RapidOCR

        params = params_for(args.child, args.model_dir)
        # Do not download during a timing run or silently substitute a model.
        import rapidocr
        from rapidocr.inference_engine.base import FileInfo, InferSession
        from rapidocr.utils.typings import TaskType

        model_dir = args.model_dir or Path(rapidocr.__file__).parent / "models"
        for task in ("Det", "Cls", "Rec"):
            info = InferSession.get_model_url(FileInfo(
                params[f"{task}.engine_type"], params[f"{task}.ocr_version"],
                TaskType(task.lower()), params[f"{task}.lang_type"],
                params[f"{task}.model_type"]))
            path = model_dir / Path(info["model_dir"]).name
            if not path.is_file():
                raise FileNotFoundError(f"Pre-download model before benchmarking: {path}")
            with path.open("rb") as model_file:
                digest = hashlib.file_digest(model_file, "sha256").hexdigest()
            if digest != info["SHA256"]:
                raise ValueError(f"Model checksum mismatch: {path}")
            # Explicit verified paths prevent any automatic model downloads.
            params[f"{task}.model_path"] = str(path)
        ocr = RapidOCR(params=params)
        init_ms = (time.perf_counter() - cold_start) * 1000
        records = []
        for name, image, expected in cases:
            start = time.perf_counter()
            result = ocr(image)
            elapsed = (time.perf_counter() - start) * 1000
            text = "\n".join(result.txts or [])
            expected_compact = "".join(expected.split())
            actual_compact = "".join(text.split())
            records.append({"case": name, "expected": expected, "actual": text,
                            "elapsed_ms": round(elapsed, 2),
                            "edit_errors": edit_distance(expected, text),
                            "compact_errors": edit_distance(expected_compact, actual_compact),
                            "compact_chars": len(expected_compact)})
        start = time.perf_counter()
        ocr(cases[0][1])
        repeat_ms = (time.perf_counter() - start) * 1000
        report = {"profile": args.child, "rapidocr": importlib.metadata.version("rapidocr"),
                  "onnxruntime": importlib.metadata.version("onnxruntime"),
                  "logical_cpus": os.cpu_count(), "cold_import_and_init_ms": round(init_ms, 2),
                  "first_inference_ms": records[0]["elapsed_ms"],
                  "median_inference_ms": round(statistics.median(r["elapsed_ms"] for r in records), 2),
                  "repeat_first_case_ms": round(repeat_ms, 2),
                  "sampled_peak_private_mib": round(max(samples) / 1048576, 1),
                  "compact_errors": sum(r["compact_errors"] for r in records),
                  "compact_chars": sum(r["compact_chars"] for r in records), "cases": records}
        (args.output / f"{args.child}.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({k: v for k, v in report.items() if k != "cases"}, ensure_ascii=False), flush=True)
    finally:
        stopped.set()
        sampler.join()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    profiles = ("baseline", "small-1", "small-2", "small-4", "small-8", "rec-medium-4", "medium-4",
                "screen-small-4", "screen-rec-medium-4", "screen-medium-4",
                "screen-balanced-4", "screen-balanced-8", "production")
    parser.add_argument("--profiles", nargs="+", choices=profiles, default=["baseline", "production"])
    parser.add_argument("--child", choices=profiles, help=argparse.SUPPRESS)
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    if args.child:
        child_run(args)
        return
    for profile in args.profiles:
        cmd = [sys.executable, str(Path(__file__).resolve()), "--child", profile, "--output", str(args.output)]
        if args.model_dir:
            cmd.extend(["--model-dir", str(args.model_dir)])
        subprocess.run(cmd, check=True, timeout=300)


if __name__ == "__main__":
    main()
