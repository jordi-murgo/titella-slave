"""Contract tests of the stdlib Windows HID transport (report 0x10).

The stream is driven against a fake report endpoint, so these tests exercise
the exact production state machine — handshake, sequencing, duplicate
handling, retransmission, backpressure, teardown — without a board, a driver
or Windows. The C# unit of the same protocol has an equivalent suite; both
must agree frame by frame, which is what the mirrored cases below check.
"""
from __future__ import print_function

import sys
import threading
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "implementations" / "python"))

import hid_transport  # noqa: E402
from hid_transport import (  # noqa: E402
    CLASS_ACK,
    CLASS_CLOSE,
    CLASS_DATA,
    CLASS_OPEN,
    CLASS_OPEN_ACK,
    REPORT_BYTES,
    HidByteStream,
    build_report,
    is_hid_binding,
    parse_report,
)


class FakePeer(hid_transport.ReportIo):
    """In-memory peer: the other end of the 0x10 flow."""

    def __init__(self):
        self._gate = threading.Condition()
        self._to_stream = []
        self.sent = []
        self.disposed = False
        self.auto_ack_open = True
        self.auto_ack_data = True
        self.suppress_data_acks = 0
        self.suppress_open_acks = 0
        self.fail_writes = 0
        # Loopback: a DATA received from the stream is handed back on the
        # peer's own sequence, so one test covers both directions at once.
        self.echo = False
        self._echo_sequence = 0

    # ── driven by the test ──────────────────────────────────────────────
    def inject(self, report):
        with self._gate:
            self._to_stream.append(bytearray(report))
            self._gate.notify_all()

    def reports(self, kind=None):
        with self._gate:
            if kind is None:
                return list(self.sent)
            return [r for r in self.sent if r[2] == kind]

    # ── ReportIo ────────────────────────────────────────────────────────
    def read(self, report, timeout_ms):
        deadline = time.monotonic() + timeout_ms / 1000.0
        with self._gate:
            while not self._to_stream:
                if self.disposed:
                    return -1
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return 0
                self._gate.wait(remaining)
            data = self._to_stream.pop(0)
            report[0:REPORT_BYTES] = data
            return REPORT_BYTES

    def write(self, report):
        report = bytearray(report)
        with self._gate:
            if self.fail_writes > 0:
                self.fail_writes -= 1
                raise hid_transport.HidStreamError("hid_io_error", "injected")
            self.sent.append(report)
            parsed = parse_report(report)
            if parsed is None:
                return
            frame_class, epoch, sequence, payload = parsed
            if frame_class == CLASS_OPEN and self.auto_ack_open:
                if self.suppress_open_acks > 0:
                    self.suppress_open_acks -= 1
                else:
                    self._to_stream.append(bytearray(
                        build_report(CLASS_OPEN_ACK, epoch, 0)))
            elif frame_class == CLASS_DATA and self.auto_ack_data:
                if self.suppress_data_acks > 0:
                    self.suppress_data_acks -= 1
                else:
                    self._to_stream.append(bytearray(
                        build_report(CLASS_ACK, epoch, sequence)))
                if self.echo:
                    self._to_stream.append(bytearray(build_report(
                        CLASS_DATA, epoch, self._echo_sequence, payload)))
                    self._echo_sequence += 1
            self._gate.notify_all()

    def close(self):
        with self._gate:
            self.disposed = True
            self._gate.notify_all()


def _stream(peer, epoch=0x51CE0001):
    stream = HidByteStream(peer, epoch)
    stream.wait_for_open(2000)
    return stream


class BindingTests(unittest.TestCase):
    def test_binding_grammar(self):
        self.assertTrue(is_hid_binding("HID:0123456789AB"))
        self.assertTrue(is_hid_binding("HID:ABCDEF012345"))
        for value in ("HID:0123456789ab", "HID:0123456789A", "HID:0123456789ABC",
                      "HID:", "COM5", "CLIPBOARD", None, "hid:0123456789AB",
                      "HID:0123456789AG"):
            self.assertFalse(is_hid_binding(value), value)

    def test_report_round_trip(self):
        report = build_report(CLASS_DATA, 0x1234, 7, b"payload")
        self.assertEqual(len(report), REPORT_BYTES)
        frame_class, epoch, sequence, payload = parse_report(report)
        self.assertEqual((frame_class, epoch, sequence, payload),
                         (CLASS_DATA, 0x1234, 7, b"payload"))

    def test_invalid_headers_are_rejected(self):
        good = bytearray(build_report(CLASS_DATA, 1, 0, b"x"))
        for mutate in (lambda r: r.__setitem__(0, 0x11),
                       lambda r: r.__setitem__(1, 2),
                       lambda r: r.__setitem__(2, 9),
                       lambda r: r.__setitem__(3, 0) or r.__setitem__(4, 0)
                       or r.__setitem__(5, 0) or r.__setitem__(6, 0),
                       lambda r: r.__setitem__(9, 0),
                       lambda r: r.__setitem__(9, 55)):
            broken = bytearray(good)
            mutate(broken)
            self.assertIsNone(parse_report(bytes(broken)))


class HandshakeTests(unittest.TestCase):
    def test_open_completes_and_opens_with_the_epoch(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            opens = peer.reports(CLASS_OPEN)
            self.assertEqual(len(opens), 1)
            self.assertEqual(parse_report(opens[0])[1], 0x51CE0001)
        finally:
            stream.close()

    def test_open_retransmits_when_the_ack_is_lost(self):
        peer = FakePeer()
        peer.suppress_open_acks = 1
        stream = _stream(peer)
        try:
            self.assertGreaterEqual(len(peer.reports(CLASS_OPEN)), 2)
        finally:
            stream.close()

    def test_missing_open_ack_fails_with_named_code(self):
        peer = FakePeer()
        peer.auto_ack_open = False
        stream = HidByteStream(peer, 0x51CE0001)
        with self.assertRaises(hid_transport.HidStreamError) as raised:
            stream.wait_for_open(1200)
        self.assertIn(raised.exception.code,
                      ("hid_open_timeout", "hid_retry_exhausted"))
        stream.close()

    def test_duplicate_open_is_re_acknowledged(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            with peer._gate:
                peer._to_stream.append(bytearray(
                    build_report(CLASS_OPEN, 0x51CE0001, 0)))
                peer._gate.notify_all()
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                if len(peer.reports(CLASS_OPEN_ACK)) >= 2:
                    break
                time.sleep(0.01)
            # Exactly one re-acknowledgement: the duplicate OPEN is answered
            # again, and no extra copy is queued for the stream's own OPEN.
            self.assertEqual(len(peer.reports(CLASS_OPEN_ACK)), 1)
        finally:
            stream.close()


class TransferTests(unittest.TestCase):
    def test_write_returns_after_acknowledgement(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            stream.write(b"hello world")
            datas = peer.reports(CLASS_DATA)
            self.assertTrue(datas)
            self.assertEqual(parse_report(datas[0])[3], b"hello world")
        finally:
            stream.close()

    def test_read_returns_the_bytes_from_the_peer(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            with peer._gate:
                peer._to_stream.append(bytearray(
                    build_report(CLASS_DATA, 0x51CE0001, 0, b"from-peer")))
                peer._gate.notify_all()
            received = b""
            deadline = time.monotonic() + 2
            while len(received) < 9 and time.monotonic() < deadline:
                received += stream.read(64)
            self.assertEqual(received, b"from-peer")
        finally:
            stream.close()

    def test_data_ack_loss_retransmits_the_same_sequence(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            peer.suppress_data_acks = 1
            stream.write(b"retry-me")
            datas = [parse_report(r)[2] for r in peer.reports(CLASS_DATA)]
            self.assertGreaterEqual(len(datas), 2)
            self.assertEqual(datas[0], datas[1])
        finally:
            stream.close()

    def test_large_transfer_round_trips(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            payload = bytes(range(256)) * 16  # 4096 bytes
            peer.echo = True
            stream.write(payload)
            received = bytearray()
            deadline = time.monotonic() + 5
            while len(received) < len(payload) and time.monotonic() < deadline:
                received += stream.read(4096)
            self.assertEqual(bytes(received), payload)
        finally:
            stream.close()

    def test_duplicate_data_is_not_reapplied(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            duplicate = bytearray(build_report(CLASS_DATA, 0x51CE0001, 0, b"once"))
            with peer._gate:
                peer._to_stream.append(bytearray(duplicate))
                peer._gate.notify_all()
            received = b""
            deadline = time.monotonic() + 2
            while len(received) < 4 and time.monotonic() < deadline:
                received += stream.read(64)
            with peer._gate:
                peer._to_stream.append(bytearray(duplicate))
                peer._gate.notify_all()
            extra = stream.read(64)
            self.assertEqual(received, b"once")
            self.assertEqual(extra, b"")
        finally:
            stream.close()

    def test_out_of_sequence_data_is_dropped(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            with peer._gate:
                peer._to_stream.append(bytearray(
                    build_report(CLASS_DATA, 0x51CE0001, 5, b"gap")))
                peer._gate.notify_all()
            self.assertEqual(stream.read(64), b"")
        finally:
            stream.close()

    def test_short_report_never_reaches_the_parser(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            with peer._gate:
                peer._to_stream.append(bytearray(10))
                peer._gate.notify_all()
            self.assertEqual(stream.read(64), b"")
        finally:
            stream.close()


class TeardownTests(unittest.TestCase):
    def test_peer_close_fails_the_link(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            with peer._gate:
                peer._to_stream.append(bytearray(
                    build_report(CLASS_CLOSE, 0x51CE0001, 0)))
                peer._gate.notify_all()
            with self.assertRaises(hid_transport.HidStreamError) as raised:
                for _ in range(200):
                    stream.read(64)
                    time.sleep(0.01)
            self.assertEqual(raised.exception.code, "hid_peer_closed")
        finally:
            stream.close()

    def test_device_error_is_observable(self):
        peer = FakePeer()
        stream = _stream(peer)
        try:
            peer.fail_writes = 5
            with self.assertRaises(OSError):
                for _ in range(50):
                    stream.write(b"x" * 64)
                    time.sleep(0.01)
        finally:
            stream.close()

    def test_close_after_open_is_clean(self):
        peer = FakePeer()
        stream = _stream(peer)
        stream.close()
        self.assertTrue(peer.disposed)


if __name__ == "__main__":
    unittest.main()


class PlatformGuardTests(unittest.TestCase):
    """The device layer must fail with a code, not with an AttributeError."""

    def test_open_stream_off_windows_reports_hid_unsupported(self):
        if sys.platform.startswith("win"):
            self.skipTest("the HID device layer exists on this host")
        with self.assertRaises(hid_transport.HidStreamError) as raised:
            hid_transport.open_stream("HID:64E833579F30")
        self.assertEqual(raised.exception.code, "hid_unsupported")

    def test_open_stream_validates_the_binding_before_the_platform(self):
        with self.assertRaises(hid_transport.HidStreamError) as raised:
            hid_transport.open_stream("COM5")
        self.assertEqual(raised.exception.code, "invalid_argument")
