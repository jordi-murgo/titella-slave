from __future__ import annotations

import os
import unittest

from titella_slave_assets.wire import (
    DATA_CHUNK_BYTES,
    FIRMWARE_TO_MASTER,
    MASTER_TO_FIRMWARE,
    MAX_FRAME_BYTES,
    Deduplicator,
    FrameParser,
    WireError,
    base_frame,
    data_b64,
    decode_data,
    encode_frame,
    validate_frame,
)

LINK = "0123456789abcdef0123456789abcdef"


class WireProtocolTests(unittest.TestCase):
    def test_frame_split_at_every_byte_and_concatenated_frames(self) -> None:
        first = base_frame(LINK, "hid", "response", request_id=1, op="health", body={"ok": True})
        second = base_frame(LINK, "control", "heartbeat")
        payload = encode_frame(first) + encode_frame(second).replace(b"\n", b"\r\n")
        for split in range(len(payload) + 1):
            parser = FrameParser(FIRMWARE_TO_MASTER)
            frames = parser.feed(payload[:split]) + parser.feed(payload[split:])
            self.assertEqual(frames, [first, second], f"split={split}")

    def test_banner_invalid_utf8_and_blank_lines_do_not_hide_next_frame(self) -> None:
        frame = base_frame(LINK, "control", "heartbeat")
        parser = FrameParser(FIRMWARE_TO_MASTER)
        frames = parser.feed(b"ESP-ROM boot\n\xff\xfe\n\n" + encode_frame(frame))
        self.assertEqual(frames, [frame])
        self.assertEqual([error.code for error in parser.errors], ["invalid_json", "invalid_json"])

    def test_utf8_body_and_arbitrary_binary_chunk_round_trip(self) -> None:
        request = base_frame(LINK, "fs", "request", request_id=7, op="write", body={"nombre": "á/東京"})
        self.assertEqual(FrameParser(MASTER_TO_FIRMWARE).feed(encode_frame(request)), [request])
        binary = bytes(range(256)) + os.urandom(DATA_CHUNK_BYTES - 256)
        data = base_frame(LINK, "shell", "data", session_id="pty", seq=3, data_b64=data_b64(binary))
        self.assertEqual(decode_data(data), binary)

    def test_exact_maximum_is_accepted_and_next_byte_overflow_resynchronizes(self) -> None:
        template = base_frame(LINK, "fs", "request", request_id=1, op="write", body={"padding": ""})
        overhead = len(encode_frame(template)) - 1
        template["body"]["padding"] = "x" * (MAX_FRAME_BYTES - overhead)
        encoded = encode_frame(template)
        self.assertEqual(len(encoded) - 1, MAX_FRAME_BYTES)
        self.assertEqual(FrameParser(MASTER_TO_FIRMWARE).feed(encoded), [template])

        valid = base_frame(LINK, "control", "heartbeat")
        parser = FrameParser(FIRMWARE_TO_MASTER)
        frames = parser.feed(b"x" * (MAX_FRAME_BYTES + 1) + b"\n" + encode_frame(valid))
        self.assertEqual(frames, [valid])
        self.assertEqual(parser.errors[-1].code, "frame_too_large")

    def test_rejects_version_channel_type_shape_and_direction(self) -> None:
        cases = [
            ({"v": 2, "channel": "control", "type": "heartbeat", "link_id": LINK}, "unsupported_version"),
            ({"v": 1, "channel": "unknown", "type": "heartbeat", "link_id": LINK}, "invalid_channel"),
            ({"v": 1, "channel": "control", "type": "wat", "link_id": LINK}, "invalid_type"),
            ({"v": 1, "channel": "hid", "type": "request", "link_id": LINK, "op": "health"}, "invalid_frame"),
        ]
        for frame, code in cases:
            with self.subTest(code=code), self.assertRaises(WireError) as raised:
                validate_frame(frame)
            self.assertEqual(raised.exception.code, code)
        with self.assertRaises(WireError) as raised:
            validate_frame(base_frame(LINK, "hid", "request", request_id=1, op="health"), FIRMWARE_TO_MASTER)
        self.assertEqual(raised.exception.code, "direction_denied")
        with self.assertRaises(WireError) as raised:
            validate_frame(base_frame(LINK, "hid", "response", request_id=1, op="health"), MASTER_TO_FIRMWARE)
        self.assertEqual(raised.exception.code, "direction_denied")

    def test_data_limits_and_deduplication_window(self) -> None:
        with self.assertRaises(WireError) as raised:
            data_b64(b"x" * (DATA_CHUNK_BYTES + 1))
        self.assertEqual(raised.exception.code, "chunk_too_large")
        dedup = Deduplicator(limit=2)
        key = (LINK, "session", 1)
        self.assertTrue(dedup.add(key))
        self.assertFalse(dedup.add(key))
        self.assertTrue(dedup.add((LINK, "session", 2)))
        self.assertTrue(dedup.add((LINK, "session", 3)))
        self.assertTrue(dedup.add(key), "the oldest entry must expire")


if __name__ == "__main__":
    unittest.main()
