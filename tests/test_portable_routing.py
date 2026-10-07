"""Frozen builds must resolve workers relative to their executable, not Python."""
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from screenlens.agent.frontend import find_frontend_exe
from screenlens.agent.workers import WorkerError, WorkerManager


class PortableRoutingTests(unittest.TestCase):
    def test_frozen_translation_uses_sibling_executable_and_requires_it(self):
        with tempfile.TemporaryDirectory(prefix="ScreenLens 测试 ") as directory:
            agent = str(Path(directory) / "ScreenLensAgent.exe")
            worker = Path(directory) / "ScreenLensWorker.exe"
            with patch.object(sys, "frozen", True, create=True), patch.object(sys, "executable", agent):
                with self.assertRaises(WorkerError):
                    WorkerManager._translation_worker_command()
                worker.touch()
                self.assertEqual(WorkerManager._translation_worker_command(), [str(worker)])

    def test_source_translation_still_uses_the_current_environment(self):
        with patch.object(sys, "frozen", False, create=True):
            self.assertEqual(WorkerManager._translation_worker_command(),
                             [WorkerManager._python_executable(), "-m", "screenlens.worker"])

    def test_frozen_frontend_uses_executable_directory_even_with_unrelated_argv(self):
        with tempfile.TemporaryDirectory() as directory:
            frontend = Path(directory) / "ScreenLens.WinUI.exe"
            frontend.touch()
            with patch.object(sys, "frozen", True, create=True), \
                    patch.object(sys, "executable", str(Path(directory) / "ScreenLensAgent.exe")), \
                    patch.object(sys, "argv", ["unrelated.py"]), patch.dict(os.environ, {"SCREENLENS_WINUI_EXE": ""}):
                self.assertEqual(find_frontend_exe(), str(frontend))

    def test_incomplete_frozen_ocr_reports_missing_package_instead_of_python_fallback(self):
        manager = WorkerManager()
        with patch.object(sys, "frozen", True, create=True), \
                patch("screenlens.agent.workers.native_ocr_command", return_value=None), \
                patch.dict(os.environ, {"SCREENLENS_OCR_BACKEND": "auto"}):
            with self.assertRaises(WorkerError) as error:
                manager._ensure_worker("ocr")
            self.assertEqual(error.exception.code, "ocr_failed")
