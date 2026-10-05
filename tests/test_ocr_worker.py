"""OCR engine lifetime and protocol results, without loading real models."""
import io
import unittest
from unittest.mock import Mock, patch

from PIL import Image

from screenlens import worker
from screenlens.ocr.engine import OcrResult


class TestOcrWorker(unittest.TestCase):
    def setUp(self):
        png = io.BytesIO()
        Image.new("RGB", (64, 32), "white").save(png, format="PNG")
        self.png = png.getvalue()

    def test_repeated_requests_reuse_engine_and_keep_result_contract(self):
        engine = Mock()
        engine.recognize.return_value = OcrResult(
            ["中文 Hello"], [0.98765], [[[1, 2], [30, 2], [30, 20], [1, 20]]])
        with patch.object(worker, "_ocr_engine", None), \
                patch("screenlens.ocr.engine.OcrEngine", return_value=engine) as factory, \
                patch.object(worker, "read_frame", return_value=self.png), \
                patch.object(worker, "_respond") as respond:
            worker._do_ocr(1)
            worker._do_ocr(2)
        factory.assert_called_once()
        self.assertEqual(engine.recognize.call_count, 2)
        for req_id, call in enumerate(respond.call_args_list, 1):
            self.assertEqual(call.args, (req_id,))
            data = call.kwargs["data"]
            self.assertEqual(data["text"], "中文 Hello")
            self.assertEqual(data["lines"], [{
                "text": "中文 Hello", "score": 0.9877,
                "box": [[1, 2], [30, 2], [30, 20], [1, 20]],
            }])
            self.assertGreaterEqual(data["elapsed_ms"], 0)

    def test_bad_image_does_not_poison_existing_engine(self):
        engine = Mock()
        engine.recognize.return_value = OcrResult(["next"], [1])
        with patch.object(worker, "_ocr_engine", engine), \
                patch.object(worker, "read_frame", side_effect=[b"not a PNG", self.png]), \
                patch.object(worker, "_respond") as respond, \
                patch.object(worker, "logger"):
            worker._do_ocr(1)
            worker._do_ocr(2)
        self.assertEqual(respond.call_args_list[0].kwargs["error_code"], "ocr_failed")
        self.assertEqual(respond.call_args_list[1].kwargs["data"]["text"], "next")
        engine.recognize.assert_called_once()

    def test_missing_frame_does_not_create_engine(self):
        with patch.object(worker, "_get_ocr_engine") as get_engine, \
                patch.object(worker, "read_frame", return_value=None), \
                patch.object(worker, "_respond") as respond:
            worker._do_ocr(1)
        get_engine.assert_not_called()
        self.assertEqual(respond.call_args.kwargs["error_code"], "bad_request")


if __name__ == "__main__":
    unittest.main()
