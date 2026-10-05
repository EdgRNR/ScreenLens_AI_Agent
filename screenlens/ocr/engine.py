# -*- coding: utf-8 -*-
"""本地 OCR 引擎封装（RapidOCR，PP-OCR 系列离线模型，支持中/英/日/混合）。"""
import logging
import os
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


def _rapidocr_params() -> dict:
    """Pinned screen-text pipeline; imports stay in the on-demand worker."""
    from rapidocr import EngineType, LangCls, ModelType, OCRVersion

    return {
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
        # Short screen selections must not be enlarged to a 736-pixel short
        # side. RapidOCR's max mode bounds detection at 960/1500/2000 pixels
        # according to input size, while recognition uses original crops.
        "Det.limit_type": "max",
        # Avoid rotating upright text on marginal direction predictions.
        # Keep high-confidence 180-degree correction for inverted images.
        "Cls.cls_thresh": 0.99,
        "EngineConfig.onnxruntime.intra_op_num_threads": min(4, os.cpu_count() or 1),
        "EngineConfig.onnxruntime.inter_op_num_threads": 1,
        "EngineConfig.onnxruntime.enable_cpu_mem_arena": False,
    }


class OcrEngine:
    """按需加载并复用模型；Agent 的空闲退出机制释放整个 worker。

    warmup_async 供旧版 UI 使用，Agent 不在启动时预热。推理串行化，
    防止共享 RapidOCR 实例的可变预处理状态被并发请求覆盖。
    """

    def __init__(self):
        self._ocr = None
        self._lock = threading.Lock()
        self._inference_lock = threading.Lock()
        self._init_error: str | None = None

    def warmup_async(self) -> None:
        threading.Thread(target=self._ensure_engine, daemon=True,
                         name="ocr-warmup").start()

    def _ensure_engine(self):
        with self._lock:
            if self._ocr is not None:
                return
            try:
                from rapidocr import RapidOCR  # 延迟导入，加快进程启动
                self._ocr = RapidOCR(params=_rapidocr_params())
                self._init_error = None
                logger.info("RapidOCR ready: PP-OCRv6 small, bounded detection, CPU threads=%d",
                            min(4, os.cpu_count() or 1))
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

        with self._inference_lock:
            return self._recognize(img)

    def _recognize(self, img: Image.Image) -> OcrResult:
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
