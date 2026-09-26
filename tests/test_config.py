# -*- coding: utf-8 -*-
"""配置模块测试。"""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from screenlens.config import Config


class TestConfig(unittest.TestCase):
    def setUp(self):
        fd, self.path = tempfile.mkstemp(suffix=".json")
        os.close(fd)
        os.remove(self.path)

    def tearDown(self):
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_default_created_on_first_load(self):
        cfg = Config(self.path)
        self.assertTrue(os.path.exists(self.path))
        self.assertEqual(cfg.hotkey, "ctrl+alt+a")
        self.assertEqual(cfg.translation["provider"], "google_free")

    def test_save_and_reload(self):
        cfg = Config(self.path)
        cfg.hotkey = "ctrl+alt+s"
        cfg.save()
        cfg2 = Config(self.path)
        self.assertEqual(cfg2.hotkey, "ctrl+alt+s")

    def test_corrupt_file_falls_back_to_default(self):
        with open(self.path, "w", encoding="utf-8") as f:
            f.write("{ this is not json !!!")
        cfg = Config(self.path)
        self.assertEqual(cfg.hotkey, "ctrl+alt+a")

    def test_partial_user_config_merged(self):
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump({"hotkey": "f9",
                       "translation": {"target_language": "en"}},
                      f, ensure_ascii=False)
        cfg = Config(self.path)
        self.assertEqual(cfg.hotkey, "f9")
        self.assertEqual(cfg.translation["target_language"], "en")
        # 未覆盖的键保留默认值
        self.assertEqual(cfg.translation["provider"], "google_free")
        self.assertIn("openai", cfg.translation)


if __name__ == "__main__":
    unittest.main()
