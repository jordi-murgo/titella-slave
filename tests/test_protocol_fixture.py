"""Shared wire-conformance corpus, consumed by Python and C#.

Protocol v1 has two independent slave implementations; both must agree on
framing, validation and payload limits. This module drives the Python codec
over the same tests/fixtures/protocol-v1.json that the C# contract suite
(tests/windows/ContractTests.cs) reads, so a divergence in either language
fails on its own side.
"""
from __future__ import annotations

import base64
import json
import unittest
from pathlib import Path

from titella_slave_assets.wire import (
    DATA_CHUNK_BYTES,
    FIRMWARE_TO_MASTER,
    FIRMWARE_TO_SLAVE,
    MASTER_TO_FIRMWARE,
    DATA_WINDOW_MAX,
    Deduplicator,
    FrameParser,
    WireError,
    data_b64,
    decode_data,
    encode_frame,
    validate_frame,
)

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "protocol-v1.json"


def load_fixture() -> dict:
    return json.loads(FIXTURE.read_text(encoding="utf-8"))


class SharedCorpusTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = load_fixture()
        self.link = self.fixture["link_id"]

    def test_every_byte_split_and_crlf_second_frame(self) -> None:
        case = self.fixture["split_at_every_byte"]
        first, second = case["first"], case["second"]
        payload = encode_frame(first)
        second_payload = encode_frame(second)
        if case["second_crlf"]:
            second_payload = second_payload.replace(b"\n", b"\r\n")
        payload += second_payload
        for split in range(len(payload) + 1):
            parser = FrameParser(FIRMWARE_TO_SLAVE)
            frames = parser.feed(payload[:split]) + parser.feed(payload[split:])
            self.assertEqual(frames, [first, second], f"split={split}")

    def test_banner_and_resync_keep_the_next_frame(self) -> None:
        case = self.fixture["banner_and_resync"]
        prefix = base64.b64decode(case["prefix_b64"])
        parser = FrameParser(FIRMWARE_TO_SLAVE)
        frames = parser.feed(prefix + encode_frame(case["frame"]))
        self.assertEqual(frames, [case["frame"]])
        self.assertEqual(
            [error.code for error in parser.errors],
            case["expected_error_codes"],
        )

    def test_unicode_body_and_binary_chunk_round_trip(self) -> None:
        case = self.fixture["utf8_round_trip"]
        request = case["frame"]
        self.assertEqual(FrameParser(MASTER_TO_FIRMWARE).feed(encode_frame(request)), [request])
        binary = bytes.fromhex(case["binary_chunk_hex"])
        data = {
            "v": 1,
            "channel": "shell",
            "type": "data",
            "link_id": self.link,
            "session_id": case["data_session_id"],
            "seq": case["data_seq"],
            "data_b64": data_b64(binary),
        }
        self.assertEqual(decode_data(data), binary)

    def test_exact_maximum_is_accepted_and_overflow_resynchronizes(self) -> None:
        case = self.fixture["exact_maximum"]
        template = json.loads(json.dumps(case["template"]))
        overhead = len(encode_frame(template)) - 1
        padding = case["max_frame_bytes"] - overhead
        self.assertGreaterEqual(padding, 1)
        template["body"]["padding"] = chr(case["padding_byte"]) * padding
        encoded = encode_frame(template)
        self.assertEqual(len(encoded) - 1, case["max_frame_bytes"])
        self.assertEqual(FrameParser(MASTER_TO_FIRMWARE).feed(encoded), [template])

        valid = case["valid_after_overflow"]
        parser = FrameParser(FIRMWARE_TO_SLAVE)
        frames = parser.feed(
            bytes([case["padding_byte"]]) * case["overflow_bytes"]
            + b"\n"
            + encode_frame(valid)
        )
        self.assertEqual(frames, [valid])
        self.assertEqual(parser.errors[-1].code, case["expected_overflow_code"])

    def test_rejection_matrix_matches_shared_codes(self) -> None:
        for case in self.fixture["rejections"]:
            with self.subTest(case=case["name"]):
                with self.assertRaises(WireError) as raised:
                    validate_frame(case["frame"], case["direction"])
                self.assertEqual(raised.exception.code, case["code"])

    def test_base64_rejections_and_chunk_limit(self) -> None:
        for case in self.fixture["base64_rejections"]:
            with self.subTest(case=case["name"]):
                frame = {
                    "v": 1,
                    "channel": "shell",
                    "type": "data",
                    "link_id": self.link,
                    "session_id": "pty",
                    "seq": 0,
                    "data_b64": case["value"],
                }
                with self.assertRaises(WireError) as raised:
                    decode_data(frame)
                self.assertEqual(raised.exception.code, case["code"])
        limit = self.fixture["chunk_limit"]
        with self.assertRaises(WireError) as raised:
            data_b64(b"\x00" * limit["chunk_bytes"])
        self.assertEqual(raised.exception.code, limit["code"])
        self.assertEqual(DATA_CHUNK_BYTES + 1, limit["chunk_bytes"])

    def test_dedup_sequence_and_seen_does_not_register(self) -> None:
        case = self.fixture["dedup_sequence"]
        dedup = Deduplicator(limit=case["limit"])
        for operation in case["operations"]:
            key = (operation["link_id"], operation["session_id"], operation["seq"])
            self.assertEqual(
                dedup.add(key), operation["add"], f"seq={operation['seq']}"
            )
        probe = case["seen_without_registering"]
        key = (probe["link_id"], probe["session_id"], probe["seq"])
        self.assertFalse(dedup.seen(key))
        self.assertTrue(dedup.add(key), "seen must not have registered the key")
        self.assertTrue(dedup.seen(key))

    def test_window_bounds_match_the_contract(self) -> None:
        case = self.fixture["window_bounds"]
        self.assertEqual(case["default"], 8)
        self.assertEqual(case["maximum"], 64)
        self.assertIn(1, case["valid"])
        for invalid in case["invalid_fallbacks"]:
            self.assertNotIn(invalid, case["valid"])


    def test_transfer_window_corpus(self) -> None:
        """The shared corpus fixes the transfer-level expectations both
        implementations must satisfy: windows 1/8/64, empty transfer, final
        partial block, a lost ACK re-applied once, and retry exhaustion."""
        case = self.fixture["transfer_window_corpus"]
        self.assertEqual(case["block_bytes"], DATA_CHUNK_BYTES)

        empty = case["empty_transfer"]
        self.assertEqual(empty["size"], 0)
        self.assertEqual(empty["blocks"], 0)
        import hashlib

        self.assertEqual(hashlib.sha256(b"").hexdigest(), empty["sha256"])

        partial = case["final_partial_block"]
        blocks = (partial["size"] + DATA_CHUNK_BYTES - 1) // DATA_CHUNK_BYTES
        self.assertEqual(blocks, partial["blocks"])
        self.assertEqual(
            partial["size"] - (partial["blocks"] - 1) * DATA_CHUNK_BYTES,
            partial["last_block_bytes"],
        )

        for entry in case["windows"]:
            self.assertIn(entry["window"], (1, 8, 64))
            self.assertLessEqual(entry["max_in_flight_before_ack"], DATA_WINDOW_MAX)
            self.assertEqual(entry["max_in_flight_before_ack"], entry["window"])
        self.assertEqual(case["invalid_window_falls_back_to"], 8)

        lost = case["lost_ack"]
        dedup = Deduplicator(limit=8)
        key = (self.link, lost["session_id"], lost["seq"])
        self.assertTrue(dedup.add(key), "first delivery applies")
        self.assertTrue(dedup.seen(key), "the retransmission is recognised")
        self.assertFalse(dedup.add(key), "the retransmission is not applied twice")
        self.assertEqual(lost["expected_applications"], 1)

        retry = case["retry_exhaustion"]
        self.assertEqual(retry["total_transmissions"], retry["retransmissions"] + 1)
        self.assertEqual(retry["expected_code"], "ack_timeout")


if __name__ == "__main__":
    unittest.main()
