"""Integration test: the Windows agent over the HID transport.

The agent's link, its dispatch and the 0x10 stream are exercised together by
cross-wiring two ``ReportIo`` endpoints in memory. The master side speaks the
real v1 wire protocol through ``wire.encode_frame``, so a frame the agent
cannot parse fails here exactly as it would on the rig — the HID transport is
the only simulated part.
"""
from __future__ import print_function

import sys
import threading
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "implementations" / "python"))
sys.path.insert(0, str(ROOT / "src" / "titella_slave_assets"))

import hid_transport  # noqa: E402
from hid_transport import REPORT_BYTES, HidByteStream  # noqa: E402
from wire import (  # noqa: E402
    FIRMWARE_TO_SLAVE,
    SLAVE_TO_FIRMWARE,
    FrameParser,
    base_frame,
    encode_frame,
)

LINK = "0123456789abcdef0123456789abcdef"
BINDING = "HID:64E833579F30"


class _CrossedIo(hid_transport.ReportIo):
    """One end of a crossed pair: writes land in the peer's inbox."""

    def __init__(self):
        self._gate = threading.Condition()
        self.inbox = []
        self.peer = None
        self.closed = False

    def deliver(self, report):
        with self._gate:
            self.inbox.append(bytes(report))
            self._gate.notify_all()

    def read(self, report, timeout_ms):
        deadline = time.monotonic() + timeout_ms / 1000.0
        with self._gate:
            while not self.inbox:
                if self.closed:
                    return -1
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return 0
                self._gate.wait(remaining)
            data = self.inbox.pop(0)
            report[0:REPORT_BYTES] = data
            return REPORT_BYTES

    def write(self, report):
        if self.closed:
            raise hid_transport.HidStreamError("hid_closed", "crossed link closed")
        self.peer.deliver(report)

    def close(self):
        with self._gate:
            self.closed = True
            self._gate.notify_all()


class _MasterSide(object):
    """Speaks v1 frames over one end of the crossed pair."""

    def __init__(self, stream):
        self.stream = stream
        self.parser = FrameParser(SLAVE_TO_FIRMWARE)
        self.frames = []
        self.request_id = 0

    def read_frames(self, timeout=5.0):
        """Reads until something useful arrived or the timeout expired.

        A closed peer ends the read instead of raising: the teardown order of a
        test is not what this class is asserting.
        """
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                chunk = self.stream.read(4096)
            except hid_transport.HidStreamError:
                break
            if chunk:
                self.frames.extend(self.parser.feed(chunk))
                continue
            if self.frames:
                break
        return list(self.frames)

    def send(self, kind, **fields):
        frame = base_frame(LINK, fields.pop("channel", "control"), kind, **fields)
        self.stream.write(encode_frame(frame, FIRMWARE_TO_SLAVE))
        return frame

    def request(self, channel, op, body=None):
        self.request_id += 1
        self.send("request", channel=channel, op=op, request_id=self.request_id,
                  body=body or {})
        return self.request_id


class HidAgentLinkTests(unittest.TestCase):
    def setUp(self):
        client_io = _CrossedIo()
        server_io = _CrossedIo()
        client_io.peer = server_io
        server_io.peer = client_io
        # The agent's stream: the one its link would have produced.
        # Only the master opens: the agent side is the passive endpoint of the
        # same flow, exactly like a peer waiting for its host. Two active
        # openers would each wait for an OPEN_ACK that neither sends. The
        # passive side must exist first, or the OPEN arrives nowhere.
        self.agent_stream = HidByteStream(client_io, 0, active=False)
        self.master = _MasterSide(HidByteStream(server_io, 0x51CE0002))
        self.master.stream.wait_for_open(3000)
        self._await_connected(self.agent_stream)

    @staticmethod
    def _await_connected(stream, timeout=3.0):
        """Waits until the passive endpoint has adopted the peer's OPEN."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            with stream._gate:
                if stream._state == hid_transport.STATE_CONNECTED:
                    return
            time.sleep(0.01)
        raise AssertionError("the passive HID endpoint never connected")

    def tearDown(self):
        agent = getattr(self, "agent", None)
        if agent is not None:
            agent.stop_event.set()
            agent.cleanup(remove_payload=False)
        for stream in (self.agent_stream, self.master.stream):
            try:
                stream.close()
            except Exception:
                pass

    def _agent(self):
        """The real agent, with only the device layer simulated.

        ``SlaveAgent`` is the production dispatcher: it owns the link, the
        hello and the bind handling, so a passing test covers the same code
        the payload runs on the slave.
        """
        # The payload's own entry module, imported by path: it is named
        # __main__.py inside the zipapp and loaded flat in the repository.
        import importlib.util

        spec = importlib.util.spec_from_file_location(
            "titella_agent_main", ROOT / "implementations" / "python" / "__main__.py")
        agent_main = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(agent_main)

        agent = agent_main.SlaveAgent(LINK, port=BINDING, heartbeat_s=1.0, grace_s=5.0)
        # The device layer is the only simulated part: the link gets the
        # already-open stream instead of opening the collection itself.
        agent.link._hs = self.agent_stream
        agent.link.port = BINDING
        agent.link._connected.set()
        # `run()` would start `_hid_reader_loop`, which reopens the collection
        # instead of using the injected stream. The three threads it needs are
        # therefore started here: a reader over the injected stream, the
        # writer and the heartbeat; the dispatch loop runs the real handler.
        def reader():
            while not agent.stop_event.is_set() and not agent.link._stop.is_set():
                try:
                    chunk = self.agent_stream.read(4096)
                except hid_transport.HidStreamError:
                    return
                if chunk:
                    agent.link._ingest(chunk)

        for target in (reader, agent.link._writer_loop, agent.link._heartbeat_loop,
                       agent._loop):
            threading.Thread(target=target, daemon=True).start()
        self.agent = agent
        self.agent_main = agent_main
        return agent

    def test_hello_and_bind_travel_over_the_hid_stream(self):
        agent = self._agent()
        try:
            from types import SimpleNamespace
            from unittest import mock

            # Exercise the actual hello emitted on Windows, not a synthetic
            # frame with the expected implementation already filled in.
            windows_os = SimpleNamespace(name="nt", getpid=lambda: 42)
            with mock.patch.object(self.agent_main, "os", windows_os):
                agent._hello()
            frames = self.master.read_frames()
            hello = next(f for f in frames if f.get("type") == "hello")
            self.assertEqual(hello["body"]["implementation"], "windows-python")

            self.master.request("control", "bind", {"link_id": LINK})
            deadline = time.monotonic() + 5
            bound = None
            while time.monotonic() < deadline:
                for frame in self.master.read_frames(0.5):
                    if frame.get("type") == "response" and frame.get("op") == "bind":
                        bound = frame
                        break
                if bound:
                    break
            self.assertIsNotNone(bound, "the agent never answered control.bind")
            self.assertEqual(bound["body"]["status"], "ready")
        finally:
            agent.stop_event.set()

    def test_payload_rides_the_data_channel_with_acks(self):
        # A 2 KiB response streamed in 512-byte data frames: the agent's
        # windowed sender and the master's ack path must agree.
        agent = self._agent()
        try:
            payload = bytes(range(256)) * 8
            stream_id = "s" * 32
            threading.Thread(
                target=agent.link.stream_data,
                args=("fs", stream_id, self._chunks(payload)),
                daemon=True,
            ).start()
            received = bytearray()
            deadline = time.monotonic() + 10
            while len(received) < len(payload) and time.monotonic() < deadline:
                for frame in self.master.read_frames(0.3):
                    if frame.get("type") == "data":
                        import base64
                        received += base64.b64decode(frame["data_b64"])
                        self.master.send("ack", channel="fs",
                                         session_id=frame["session_id"],
                                         ack_seq=frame["seq"])
            self.assertEqual(bytes(received), payload)
        finally:
            agent.stop_event.set()

    @staticmethod
    def _chunks(payload):
        position = [0]

        def read_chunk():
            start = position[0]
            if start >= len(payload):
                return b""
            position[0] = start + 512
            return payload[start:start + 512]

        return read_chunk


if __name__ == "__main__":
    unittest.main()
