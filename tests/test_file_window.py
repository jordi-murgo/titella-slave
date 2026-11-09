"""Regression tests for the slave side of the file-transfer window.

Covers the TransferService receivers against a local protocol peer: the
idempotent upload receiver, the exact commit of empty and partial files and
the download window threaded from the request. It also drives the real
zipapp receiver over a PTY: a gap is neither acknowledged nor written, the
resend of the same block is accepted once and the download honours window 1
and 8.
"""
from __future__ import annotations

import hashlib
import os
import pty
import select
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

from build_support import build
from implementations.python.transfer_service import TransferService
from titella_slave_assets.wire import (
    DATA_WINDOW_DEFAULT,
    FIRMWARE_TO_SLAVE,
    SLAVE_TO_FIRMWARE,
    FrameParser,
    base_frame,
    data_b64,
    decode_data,
    encode_frame,
)


LINK = "0123456789abcdef0123456789abcdef"


class CapturingLink:
    """Minimal agent link that records the requested window."""

    def __init__(self) -> None:
        self.windows: list[int] = []
        self.events: list[tuple[str, str, str, dict]] = []
        self.condition = threading.Condition()

    def stream_data(self, channel, session_id, read_chunk, window=8, on_ack=None, stop_check=None):
        self.windows.append(window)
        acked = 0
        while True:
            chunk = read_chunk()
            if not chunk:
                return acked
            acked += len(chunk)
            if on_ack is not None:
                on_ack(acked)

    def event(self, channel, op, session_id, body):
        with self.condition:
            self.events.append((channel, op, session_id, body))
            self.condition.notify_all()

    def wait_event(self, op: str, session_id: str, timeout: float = 5) -> dict:
        deadline = time.monotonic() + timeout
        with self.condition:
            while time.monotonic() < deadline:
                for event in self.events:
                    if event[1] == op and event[2] == session_id:
                        return event[3]
                self.condition.wait(deadline - time.monotonic())
        raise TimeoutError(op)


class TransferServiceTests(unittest.TestCase):
    def test_upload_receiver_is_idempotent_and_rejects_gaps(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            service = TransferService(CapturingLink())
            final = Path(temp) / "target.bin"
            payload = os.urandom(1536)
            checksum = hashlib.sha256(payload).hexdigest()
            service.begin_upload("up", str(final), len(payload), checksum)
            service.upload_data("up", 0, payload[0:512])
            with self.assertRaises(Exception) as raised:
                service.upload_data("up", 2, payload[1024:1536])
            self.assertEqual(getattr(raised.exception, "code", ""), "sequence")
            self.assertFalse(final.exists())
            # A block already written is ignored idempotently.
            service.upload_data("up", 0, payload[0:512])
            service.upload_data("up", 1, payload[512:1024])
            service.upload_data("up", 2, payload[1024:1536])
            result = service.commit_upload("up")
            self.assertEqual(result["status"], "finished")
            self.assertEqual(final.read_bytes(), payload)

    def test_empty_and_partial_files_commit_exactly(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            service = TransferService(CapturingLink())
            empty = Path(temp) / "empty.bin"
            service.begin_upload("empty", str(empty), 0, hashlib.sha256(b"").hexdigest())
            self.assertEqual(service.commit_upload("empty")["status"], "finished")
            self.assertEqual(empty.read_bytes(), b"")

            payload = os.urandom(520)
            partial = Path(temp) / "partial.bin"
            service.begin_upload(
                "partial", str(partial), len(payload), hashlib.sha256(payload).hexdigest(),
            )
            for sequence, position in enumerate(range(0, len(payload), 512)):
                service.upload_data("partial", sequence, payload[position:position + 512])
            self.assertEqual(service.commit_upload("partial")["status"], "finished")
            self.assertEqual(partial.read_bytes(), payload)

    def test_download_window_is_threaded_from_the_request(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            link = CapturingLink()
            service = TransferService(link)
            source = Path(temp) / "source.bin"
            source.write_bytes(os.urandom(3000))
            checksum = hashlib.sha256(source.read_bytes()).hexdigest()
            service.begin_download("d1", str(source), checksum, window=1)
            link.wait_event("transfer.finished", "d1")
            service.begin_download("d8", str(source), checksum)
            link.wait_event("transfer.finished", "d8")
            self.assertEqual(link.windows, [1, 8])
            self.assertEqual(DATA_WINDOW_DEFAULT, 8)


class SlavePeer:
    """Simulated master over the PTY of the real zipapp."""

    def __init__(self, fd: int) -> None:
        self.fd = fd
        self.parser = FrameParser(SLAVE_TO_FIRMWARE)
        self.frames: list[dict] = []
        self.request_id = 0
        self.received: dict[str, dict[int, bytes]] = {}

    def send(self, frame: dict) -> None:
        payload = encode_frame(frame, FIRMWARE_TO_SLAVE)
        total = 0
        while total < len(payload):
            total += os.write(self.fd, payload[total:])

    def next_frame(self, timeout: float = 5) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.frames:
                return self.frames.pop(0)
            readable, _, _ = select.select([self.fd], [], [], max(0, deadline - time.monotonic()))
            if not readable:
                continue
            try:
                chunk = os.read(self.fd, 4096)
            except OSError:
                # The other end may disappear when the session closes; it is
                # retried until the deadline instead of failing immediately.
                time.sleep(0.01)
                continue
            if not chunk:
                # Transient EOF: on a PTY it can happen before the
                # subprocess opens its descriptor. It waits until the deadline.
                time.sleep(0.01)
                continue
            self.frames.extend(self.parser.feed(chunk))
        raise TimeoutError("the agent has not sent the expected frame")

    def consume(self, frame: dict, ack_data: bool = True) -> None:
        if frame["type"] != "data":
            return
        session_id = frame["session_id"]
        self.received.setdefault(session_id, {})[frame["seq"]] = decode_data(frame)
        if ack_data:
            self.send(base_frame(
                LINK, frame["channel"], "ack",
                session_id=session_id, ack_seq=frame["seq"],
            ))

    def request(self, channel: str, op: str, body: dict | None = None,
                timeout: float = 8, ack_data: bool = True) -> dict:
        self.request_id += 1
        request_id = self.request_id
        self.send(base_frame(
            LINK, channel, "request", request_id=request_id, op=op, body=body or {},
        ))
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            if frame.get("request_id") == request_id and frame["type"] in {"response", "error"}:
                if frame["type"] == "error":
                    raise AssertionError("remote error: %r" % (frame.get("body"),))
                return dict(frame.get("body") or {})
            self.consume(frame, ack_data=ack_data)
        raise TimeoutError("no response for %s.%s" % (channel, op))

    def send_data(self, channel: str, session_id: str, sequence: int, data: bytes) -> None:
        self.send(base_frame(
            LINK, channel, "data", session_id=session_id, seq=sequence, data_b64=data_b64(data),
        ))

    def wait_frame(self, predicate, timeout: float = 5) -> dict:
        """Next frame matching ``predicate``; heartbeats are ignored."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            if frame["type"] == "heartbeat":
                continue
            if predicate(frame):
                return frame
            self.consume(frame)
        raise TimeoutError("without the expected frame")

    def wait_ack(self, session_id: str, sequence: int, timeout: float = 5) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            if frame["type"] == "ack" and frame["session_id"] == session_id and frame["ack_seq"] == sequence:
                return
            self.consume(frame)
        raise TimeoutError("no ACK for %s:%d" % (session_id, sequence))

    def wait_event(self, op: str, session_id: str, timeout: float = 10) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            if frame["type"] == "event" and frame.get("op") == op and frame.get("session_id") == session_id:
                return dict(frame.get("body") or {})
            self.consume(frame)
        raise TimeoutError("no event %s for %s" % (op, session_id))


class SlaveReceiverPtyTests(unittest.TestCase):
    """Real zipapp receiver: gap without a false ACK and resend accepted."""

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        agent = self.root / "titella-slave.pyz"
        build(agent)
        runtime = shutil.which("python3.9") or sys.executable
        self.master, slave = pty.openpty()
        self.slave = slave
        # The slave end is retained until the agent greets: closing it
        # earlier leaves the agent without its PTY (the open fails and it exits without hello).
        self.addCleanup(self._close_if_open, "master")
        self.addCleanup(self._close_if_open, "slave")
        self.process = None
        self._output: tuple[bytes, bytes] | None = None
        # Register the subprocess cleanup BEFORE waiting for the greeting: if
        # setUp fails, tearDown does not run and the process would stay alive.
        self.addCleanup(self._stop_agent)
        environment = dict(os.environ)
        environment.pop("PYTHONPATH", None)
        self.process = subprocess.Popen(
            [runtime, str(agent), "--port", os.ttyname(slave), "--link-id", LINK],
            cwd=self.root,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.peer = SlavePeer(self.master)
        deadline = time.monotonic() + 8
        while True:
            if self.process.poll() is not None:
                self._raise_with_stderr("the agent exited before greeting")
            try:
                hello = self.peer.next_frame(0.2)
                break
            except TimeoutError:
                if time.monotonic() >= deadline:
                    self._raise_with_stderr("the agent did not greet over the PTY")
        self.assertEqual((hello["channel"], hello["type"]), ("control", "hello"))
        self.assertEqual(
            self.peer.request("control", "bind", {"link_id": LINK})["status"], "ready",
        )
        # From here on the agent has the PTY open and the slave end can be closed.
        self._close_if_open("slave")

    def _close_if_open(self, name: str) -> None:
        fd = getattr(self, name, -1)
        if fd is not None and fd >= 0:
            try:
                os.close(fd)
            except OSError:
                pass
            setattr(self, name, -1)

    def _stop_agent(self) -> tuple[bytes, bytes]:
        """Terminates the subprocess once and caches its output."""
        if self.process is not None:
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(3)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(3)
            if self._output is None:
                self._output = self.process.communicate()
        return self._output or (b"", b"")

    def _raise_with_stderr(self, message: str) -> None:
        """Startup failure with the real subprocess output, not an opaque timeout."""
        _, stderr = self._stop_agent()
        self.fail(f"{message}: {stderr.decode('utf-8', 'replace')}")

    def tearDown(self) -> None:
        stdout, stderr = self._stop_agent()
        self._close_if_open("master")
        self.assertEqual(stdout, b"")
        if self._outcome.success:
            self.assertEqual(stderr, b"")

    def test_gap_is_rejected_without_ack_and_retry_is_accepted_once(self) -> None:
        payload = os.urandom(1536)
        checksum = hashlib.sha256(payload).hexdigest()
        remote = self.root / "uploaded.bin"
        self.peer.request("transfer", "upload.begin", {
            "transfer_id": "up", "path": str(remote), "size": len(payload), "sha256": checksum,
        })
        self.peer.send_data("transfer", "up", 0, payload[0:512])
        self.peer.wait_ack("up", 0)

        # Gap: block 2 arrives before block 1. It is neither acknowledged nor written.
        self.peer.send_data("transfer", "up", 2, payload[1024:1536])
        error = self.peer.wait_frame(
            lambda frame: frame["channel"] == "transfer"
            and frame["type"] in {"error", "ack"},
        )
        self.assertEqual(error["type"], "error")
        self.assertEqual(error["body"]["code"], "sequence")
        # Resending the same rejected block cannot be acknowledged either: with the
        # dedup key registered before accepting, this ACK would be false and the gap
        # would go unnoticed.
        self.peer.send_data("transfer", "up", 2, payload[1024:1536])
        repeated = self.peer.wait_frame(
            lambda frame: frame["channel"] == "transfer"
            and frame["type"] in {"error", "ack"},
        )
        self.assertEqual(repeated["type"], "error")
        self.assertEqual(repeated["body"]["code"], "sequence")

        self.peer.send_data("transfer", "up", 1, payload[512:1024])
        self.peer.wait_ack("up", 1)
        self.peer.send_data("transfer", "up", 2, payload[1024:1536])
        self.peer.wait_ack("up", 2)
        committed = self.peer.request("transfer", "upload.commit", {"transfer_id": "up"})
        self.assertEqual(committed["status"], "finished")
        self.assertEqual(remote.read_bytes(), payload)

    def test_download_pipelines_blocks_and_honours_window_one(self) -> None:
        payload = os.urandom(8 * 512)
        source = self.root / "source.bin"
        source.write_bytes(payload)
        checksum = hashlib.sha256(payload).hexdigest()

        started = self.peer.request("transfer", "download.begin", {
            "transfer_id": "down", "path": str(source), "checksum": checksum, "window": 8,
        }, ack_data=False)
        self.assertEqual(started["sha256"], checksum)
        # With no block acknowledged the sender must have several in flight:
        # a stop-and-wait would only send the first one.
        received = self.peer.received.setdefault("down", {})
        while len(received) < 4:
            self.peer.consume(self.peer.next_frame(5), ack_data=False)
        self.assertGreaterEqual(set(received), {0, 1, 2, 3})
        for seq in list(received):
            self.peer.send(base_frame(
                LINK, "transfer", "ack", session_id="down", ack_seq=seq,
            ))
        done = self.peer.wait_event("transfer.finished", "down")
        self.assertEqual(done["status"], "finished")

        # window=1 falls back to stop-and-wait: only one unacknowledged block.
        single = self.peer.request("transfer", "download.begin", {
            "transfer_id": "single", "path": str(source), "checksum": checksum, "window": 1,
        }, ack_data=False)
        self.assertEqual(single["status"], "running")
        deadline = time.monotonic() + 0.4
        while "single" not in self.peer.received:
            self.peer.consume(self.peer.next_frame(0.4), ack_data=False)
            self.assertLess(time.monotonic(), deadline, "the first block did not arrive")
        self.assertEqual(sorted(self.peer.received["single"]), [0])
        # And it must not run ahead with a second block until the ACK arrives.
        while time.monotonic() < deadline:
            try:
                frame = self.peer.next_frame(max(0.01, deadline - time.monotonic()))
            except TimeoutError:
                break
            self.peer.consume(frame, ack_data=False)
            self.assertNotEqual(
                frame.get("type"), "data", "window=1 must not run ahead with a second block",
            )
        self.peer.send(base_frame(
            LINK, "transfer", "ack", session_id="single", ack_seq=0,
        ))
        self.peer.wait_event("transfer.finished", "single")


if __name__ == "__main__":
    unittest.main()
