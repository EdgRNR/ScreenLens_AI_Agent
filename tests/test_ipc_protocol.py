import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from screenlens.ipc.protocol import (
    MAX_FRAME,
    ProtocolError,
    encode_frame,
    read_frame,
    read_json_frame,
)


class TestIpcProtocol(unittest.TestCase):
    def test_binary_frame_round_trip(self):
        payload = b"\x89PNG\r\n\x1a\nsmall image"
        encoded = encode_frame(payload)
        offset = 0

        def read_exact(length):
            nonlocal offset
            result = encoded[offset:offset + length]
            offset += len(result)
            return result

        self.assertEqual(read_frame(read_exact), payload)

    def test_rejects_oversized_frame(self):
        header = (MAX_FRAME + 1).to_bytes(4, "big")
        with self.assertRaises(ProtocolError):
            read_frame(lambda length: header if length == 4 else b"")

    def test_rejects_non_object_control_frame(self):
        payload = b"[]"
        framed = len(payload).to_bytes(4, "big") + payload
        offset = 0

        def read_exact(length):
            nonlocal offset
            result = framed[offset:offset + length]
            offset += len(result)
            return result

        with self.assertRaises(ProtocolError):
            read_json_frame(read_exact)


if __name__ == "__main__":
    unittest.main()
