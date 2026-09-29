# -*- coding: utf-8 -*-
"""按需 OCR / 翻译工作进程（短生命周期）。

由后台代理（screenlens.agent）spawn，绝不由用户直接运行：
- stdin/stdout 使用与 IPC 相同的 4 字节长度前缀帧（见 ipc.protocol）；
- 请求控制帧 {"op": "ocr", "id": n} 之后紧跟一帧 PNG 二进制；
- 请求控制帧 {"op": "translate", "id": n, "data": {...}}；
- 响应控制帧 {"ok": true, "data": {...}} / {"ok": false, "error": {...}}；
- stdout 只输出协议帧，所有诊断日志写 stderr 与日志文件；
- 父进程在任务结束后发 {"op": "exit"} 或直接 terminate，
  重内存（onnxruntime / 模型）随进程退出彻底回收。

为什么独立进程：RapidOCR + onnxruntime 加载后常驻约 100+ MiB
私有内存，放进后台代理必然击穿空闲 50 MiB 目标。
"""
from __future__ import annotations

import io
import json
import logging
import logging.handlers
import os
import sys

from screenlens.ipc.protocol import (
    encode_frame,
    read_frame,
)


def _setup_logging() -> logging.Logger:
    log_dir = os.path.join(
        os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"),
        "ScreenLens", "logs")
    try:
        os.makedirs(log_dir, exist_ok=True)
        handler = logging.handlers.RotatingFileHandler(
            os.path.join(log_dir, "worker.log"),
            maxBytes=512 * 1024, backupCount=1, encoding="utf-8")
    except OSError:
        handler = logging.StreamHandler(sys.stderr)
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        handlers=[handler, logging.StreamHandler(sys.stderr)])
    return logging.getLogger("screenlens.worker")


def _respond(req_id, *, data=None, error_code=None, message=None) -> None:
    resp = {"id": req_id, "ok": error_code is None}
    if error_code is None:
        resp["data"] = data or {}
    else:
        resp["error"] = {"code": error_code, "message": message or error_code}
    sys.stdout.buffer.write(encode_frame(
        json.dumps(resp, ensure_ascii=False).encode("utf-8")))
    sys.stdout.buffer.flush()


def _do_ocr(req_id: int) -> None:
    import time

    from PIL import Image

    from screenlens.ocr.engine import OcrEngine

    png = read_frame(_stdin_read_exact)
    if png is None:
        _respond(req_id, error_code="bad_request", message="缺少图像帧")
        return
    t0 = time.perf_counter()
    try:
        img = Image.open(io.BytesIO(png))
        img.load()
        result = OcrEngine().recognize(img)
    except Exception as e:
        logger.exception("worker OCR 失败")
        _respond(req_id, error_code="ocr_failed", message=f"OCR 识别失败：{e}")
        return
    elapsed_ms = int((time.perf_counter() - t0) * 1000)
    lines = []
    for idx, text in enumerate(result.lines):
        score = result.scores[idx] if idx < len(result.scores) else 1.0
        box = (result.boxes[idx]
               if result.boxes and idx < len(result.boxes) else None)
        lines.append({"text": text, "score": round(float(score), 4),
                      "box": box})
    _respond(req_id, data={
        "text": result.text,
        "lines": lines,
        "elapsed_ms": elapsed_ms,
    })


def _stdin_read_exact(n: int):
    data = sys.stdin.buffer.read(n)
    if data is None or len(data) < n:
        return None
    return data


def _do_translate(req_id: int, data: dict) -> None:
    from screenlens.translate.provider import (
        TranslationError,
        get_provider,
    )

    text = (data or {}).get("text") or ""
    cfg = (data or {}).get("config") or {}
    target = (data or {}).get("target_language", "zh")
    try:
        provider = get_provider(cfg)
        translated = provider.translate(text, target)
        _respond(req_id, data={"text": translated})
    except TranslationError as e:
        _respond(req_id, error_code="translate_failed", message=str(e))
    except Exception as e:
        logger.exception("worker 翻译异常")
        _respond(req_id, error_code="translate_failed", message=f"翻译失败：{e}")


def main() -> int:
    global logger
    logger = _setup_logging()
    logger.info("worker 启动 pid=%s", os.getpid())
    try:
        while True:
            frame = read_frame(_stdin_read_exact)
            if frame is None:  # 父进程关闭管道
                break
            try:
                msg = json.loads(frame.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                logger.warning("worker 收到非法帧，忽略")
                continue
            op = msg.get("op")
            req_id = msg.get("id", -1)
            if op == "exit":
                break
            elif op == "ocr":
                _do_ocr(req_id)
            elif op == "translate":
                _do_translate(req_id, msg.get("data") or {})
            else:
                _respond(req_id, error_code="unknown_op",
                         message=f"worker 不支持的操作 {op}")
    except Exception:
        logger.exception("worker 主循环异常")
        return 1
    logger.info("worker 退出")
    return 0


logger = logging.getLogger("screenlens.worker")

if __name__ == "__main__":
    sys.exit(main())
