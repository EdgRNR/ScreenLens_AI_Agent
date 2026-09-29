# -*- coding: utf-8 -*-
"""本地 OCR 引擎封装（RapidOCR，PP-OCR 系列离线模型，支持中/英/日/混合）。"""
import logging
import threading

import numpy as np
from PIL import Image

logger = logging.getLogger(__name__)


class OcrResult:
    def __init__(self, lines: list[str], scores: list[float],
                 boxes: list | None = None):
        self.lines = lines        # 按阅读顺序排列的文本行
        self.scores = scores      # 每行的置信度
        # 每行的四点轮廓 [[x, y] * 4]（图像像素坐标），可用于结果定位
        self.boxes = boxes or []

    @property
    def text(self) -> str:
        return "\n".join(self.lines)

    @property
    def is_empty(self) -> bool:
        return len(self.lines) == 0


class OcrError(Exception):
    pass


class OcrEngine:
    """懒加载 + 后台预热的单例式 OCR 引擎。

    init() 加载模型约 1 秒，在应用启动后立刻后台预热，
    避免用户第一次圈选时等待。
    """

    def __init__(self):
        self._ocr = None
        self._lock = threading.Lock()
        self._init_error: str | None = None

    def warmup_async(self) -> None:
        threading.Thread(target=self._ensure_engine, daemon=True,
                         name="ocr-warmup").start()

    def _ensure_engine(self):
        with self._lock:
            if self._ocr is not None or self._init_error:
                return
            try:
                from rapidocr import RapidOCR  # 延迟导入，加快进程启动
                self._ocr = RapidOCR()
                logger.info("RapidOCR engine ready")
            except Exception as e:  # pragma: no cover
                self._init_error = f"OCR 引擎初始化失败: {e}"
                logger.exception("OCR init failed")

    def recognize(self, img: Image.Image) -> OcrResult:
        """识别一张 PIL 图像。线程安全，可在工作线程中调用。"""
        self._ensure_engine()
        if self._init_error:
            raise OcrError(self._init_error)
        if self._ocr is None:
            raise OcrError("OCR 引擎不可用")

        if img.mode != "RGB":
            img = img.convert("RGB")
        arr = np.asarray(img)
        try:
            result = self._ocr(arr)
        except Exception as e:
            raise OcrError(f"OCR 识别出错: {e}") from e

        txts = result.txts if result.txts is not None else []
        scores = ([float(s) for s in result.scores]
                  if result.scores is not None else [1.0] * len(txts))
        boxes = []
        raw_boxes = getattr(result, "boxes", None)
        if raw_boxes is not None:
            for b in raw_boxes:
                try:
                    boxes.append([[float(p[0]), float(p[1])] for p in b])
                except Exception:
                    boxes.append(None)
        lines = [t.strip() for t in txts if t and t.strip()]
        kept_scores = [s for t, s in zip(txts, scores) if t and t.strip()]
        kept_boxes = [bx for t, bx in zip(txts, boxes) if t and t.strip()]
        return OcrResult(lines, kept_scores, kept_boxes)
