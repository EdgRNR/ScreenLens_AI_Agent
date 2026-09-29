# -*- coding: utf-8 -*-
"""配置模块测试。"""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from screenlens.config import Config, validate_config


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


class TestValidateConfig(unittest.TestCase):
    def test_valid_default(self):
        from screenlens.config import DEFAULT_CONFIG

        self.assertEqual(validate_config(DEFAULT_CONFIG), [])

    def test_bad_root_type(self):
        self.assertEqual(len(validate_config([1, 2])), 1)
        self.assertEqual(len(validate_config("x")), 1)

    def test_bad_provider(self):
        errors = validate_config({"translation": {"provider": "baidu"}})
        self.assertTrue(any("翻译提供器" in e for e in errors))

    def test_bad_language(self):
        errors = validate_config({"translation": {"target_language": "fr"}})
        self.assertTrue(any("目标语言" in e for e in errors))

    def test_bad_hotkey(self):
        errors = validate_config({"hotkey": ""})
        self.assertTrue(any("快捷键" in e for e in errors))
        errors = validate_config({"hotkey": None})
        self.assertTrue(any("快捷键" in e for e in errors))

    def test_bad_openai_url(self):
        cfg = {"translation": {"provider": "openai",
                               "openai": {"base_url": "ftp://x"}}}
        errors = validate_config(cfg)
        self.assertTrue(any("base_url" in e for e in errors))

    def test_good_openai_url(self):
        cfg = {"translation": {"provider": "openai",
                               "openai": {"base_url": "https://api.x/v1",
                                          "model": "test-model",
                                          "api_key": "test-key"}}}
        self.assertEqual(validate_config(cfg), [])


class TestConfigRobustness(unittest.TestCase):
    def setUp(self):
        fd, self.path = tempfile.mkstemp(suffix=".json")
        os.close(fd)
        os.remove(self.path)

    def tearDown(self):
        if os.path.exists(self.path):
            os.remove(self.path)

    def test_atomic_save_no_tmp_leftover(self):
        cfg = Config(self.path)
        cfg.save()
        leftovers = [f for f in os.listdir(os.path.dirname(self.path))
                     if f.startswith(".screenlens-")]
        self.assertEqual(leftovers, [])
        with open(self.path, "r", encoding="utf-8") as f:
            data = json.load(f)  # 保存后必须是合法 JSON
        self.assertIn("hotkey", data)

    def test_save_is_utf8(self):
        cfg = Config(self.path)
        cfg.save()
        with open(self.path, "rb") as f:
            raw = f.read()
        self.assertNotIn(b"\\u", raw.replace(b"\\u00", b""))  # 非 ASCII 转义
        raw.decode("utf-8")  # 能以 UTF-8 解码

    def test_corrupt_file_reports_and_falls_back(self):
        with open(self.path, "w", encoding="utf-8") as f:
            f.write("{ broken json !!!")
        cfg = Config(self.path)
        self.assertIsNotNone(cfg.load_error)
        self.assertIn("损坏", cfg.load_error)
        self.assertEqual(cfg.hotkey, "ctrl+alt+a")

    def test_non_dict_root_reports(self):
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump([1, 2, 3], f)
        cfg = Config(self.path)
        self.assertIsNotNone(cfg.load_error)
        self.assertEqual(cfg.hotkey, "ctrl+alt+a")

    def test_invalid_values_reported_but_merged(self):
        """非法取值：回退可用默认并给出提示，不崩溃。"""
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump({"hotkey": "ctrl+alt+a",
                      "translation": {"provider": "bad",
                                      "target_language": "zh"}},
                      f, ensure_ascii=False)
        cfg = Config(self.path)
        self.assertIsNotNone(cfg.load_error)
        self.assertIn("翻译提供器", cfg.load_error)
        # 合法字段保留用户值
        self.assertEqual(cfg.hotkey, "ctrl+alt+a")

    def test_old_config_upgraded(self):
        """第一阶段旧配置（无新增字段）自动补齐默认值。"""
        old_phase1 = {
            "hotkey": "ctrl+alt+s",
            "translation": {
                "provider": "openai",
                "target_language": "ja",
                "openai": {"base_url": "https://api.x/v1",
                           "api_key": "sk-test",
                           "model": "m"},
            },
        }
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump(old_phase1, f, ensure_ascii=False)
        cfg = Config(self.path)
        self.assertIsNone(cfg.load_error)
        self.assertEqual(cfg.translation["openai"]["api_key"], "sk-test")
        self.assertEqual(cfg.translation["target_language"], "ja")

    def test_set_translation(self):
        cfg = Config(self.path)
        cfg.set_translation({"provider": "none", "target_language": "en",
                             "openai": {}})
        self.assertEqual(cfg.translation["provider"], "none")


if __name__ == "__main__":
    unittest.main()
