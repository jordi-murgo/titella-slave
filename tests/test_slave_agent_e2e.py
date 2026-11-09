from __future__ import annotations

import base64
import errno
import hashlib
import os
import pty
import select
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from collections import defaultdict
from pathlib import Path

from build_support import build
from titella_slave_assets.wire import (
    FIRMWARE_TO_SLAVE,
    SLAVE_TO_FIRMWARE,
    FrameParser,
    base_frame,
    data_b64,
    decode_data,
    encode_frame,
)

LINK = "0123456789abcdef0123456789abcdef"


class MasterPeer:
    def __init__(self, fd: int) -> None:
        self.fd = fd
        self.parser = FrameParser(SLAVE_TO_FIRMWARE)
        self.request_id = 0
        self.frames: list[dict] = []
        self.events: list[dict] = []
        self.data: dict[tuple[str, str], bytearray] = defaultdict(bytearray)

    def send(self, frame: dict) -> None:
        payload = encode_frame(frame, FIRMWARE_TO_SLAVE)
        total = 0
        while total < len(payload):
            total += os.write(self.fd, payload[total:])

    def next_frame(self, timeout: float = 5) -> dict:
        if self.frames:
            return self.frames.pop(0)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            readable, _, _ = select.select([self.fd], [], [], max(0, deadline - time.monotonic()))
            if not readable:
                break
            try:
                chunk = os.read(self.fd, 4096)
            except OSError as exc:
                # Linux reports EIO on the PTY master during the short gap
                # between closing this process's slave fd and the agent opening
                # the same slave path. macOS blocks instead. Treat only that
                # documented PTY state as transient; the deadline still bounds
                # a child that never connects.
                if exc.errno == errno.EIO:
                    time.sleep(0.01)
                    continue
                raise
            parsed = self.parser.feed(chunk)
            if parsed:
                self.frames.extend(parsed[1:])
                return parsed[0]
        raise TimeoutError("the agent has not sent the expected frame")

    def consume(self, frame: dict, ack_data: bool = True) -> None:
        if frame["type"] == "data":
            stream = str(frame.get("stream", "data"))
            self.data[(frame["session_id"], stream)].extend(decode_data(frame))
            if ack_data:
                self.send(base_frame(
                    LINK,
                    frame["channel"],
                    "ack",
                    session_id=frame["session_id"],
                    ack_seq=frame["seq"],
                ))
        elif frame["type"] == "event":
            self.events.append(frame)

    def request(self, channel: str, op: str, body: dict | None = None, timeout: float = 8) -> dict:
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
            self.consume(frame)
        raise TimeoutError("no response for %s.%s" % (channel, op))

    def send_data(self, channel: str, session_id: str, sequence: int, data: bytes) -> None:
        self.send(base_frame(
            LINK, channel, "data", session_id=session_id, seq=sequence,
            data_b64=data_b64(data),
        ))
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            if frame["type"] == "ack" and frame["session_id"] == session_id and frame["ack_seq"] == sequence:
                return
            self.consume(frame)
        raise TimeoutError("no ACK from the agent")

    def wait_event(self, op: str, session_id: str, timeout: float = 10) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for frame in list(self.events):
                if frame.get("op") == op and frame.get("session_id") == session_id:
                    self.events.remove(frame)
                    return frame
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            self.consume(frame)
        raise TimeoutError("no event %s for %s" % (op, session_id))

    def wait_bytes(self, session_id: str, needle: bytes, timeout: float = 10) -> bytes:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            combined = b"".join(
                bytes(value) for (current, _), value in self.data.items() if current == session_id
            )
            if needle in combined:
                return combined
            frame = self.next_frame(max(0.01, deadline - time.monotonic()))
            self.consume(frame)
        raise TimeoutError("%r was not received" % needle)


class SlaveAgentE2ETests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.agent = self.root / "titella-slave.pyz"
        build(self.agent)
        self.runtime = shutil.which("python3.9") or sys.executable
        master, slave = pty.openpty()
        self.master = master
        slave_name = os.ttyname(slave)
        environment = dict(os.environ)
        environment.pop("PYTHONPATH", None)
        self.process = subprocess.Popen(
            [self.runtime, str(self.agent), "--port", slave_name, "--link-id", LINK],
            cwd=self.root,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        os.close(slave)
        self.peer = MasterPeer(master)
        hello = self.peer.next_frame(8)
        self.assertEqual((hello["channel"], hello["type"]), ("control", "hello"))
        body = hello["body"]
        self.assertEqual(body["agent"], "titella-slave")
        self.assertEqual(body["implementation"], "python")
        self.assertEqual(body["runtime"]["name"], "python")
        self.assertEqual(body["runtime"]["version"], body["python"])
        self.assertEqual(body["pid"], self.process.pid)
        ready = self.peer.request("control", "bind", {"link_id": LINK})
        self.assertEqual(ready["status"], "ready")

    def tearDown(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(3)
        os.close(self.master)
        stdout, stderr = self.process.communicate()
        self.assertEqual(stdout, b"")
        if self._outcome.success:
            self.assertEqual(stderr, b"")

    def test_zipapp_shell_terminal_files_and_transfers(self) -> None:
        started = self.peer.request("shell", "job.start", {
            "job_id": "quick", "cmd": "printf out; printf err >&2", "timeout": 5,
        })
        self.assertEqual(started["status"], "running")
        finished = self.peer.wait_event("job.finished", "quick")
        self.assertEqual(finished["body"]["returncode"], 0)
        self.assertEqual(bytes(self.peer.data[("quick", "stdout")]), b"out")
        self.assertEqual(bytes(self.peer.data[("quick", "stderr")]), b"err")

        terminal = self.peer.request("shell", "terminal.open", {
            "cwd": str(self.root), "rows": 24, "cols": 80, "idle_timeout": 30,
        })
        session_id = terminal["session_id"]
        self.peer.send_data("shell", session_id, 0, b"printf 'MARK:'; pwd; stty size\n")
        output = self.peer.wait_bytes(session_id, b"MARK:")
        output = self.peer.wait_bytes(session_id, b"24 80")
        self.assertIn(str(self.root).encode(), output)
        resized = self.peer.request("shell", "terminal.resize", {
            "session_id": session_id, "rows": 31, "cols": 99,
        })
        self.assertEqual((resized["rows"], resized["cols"]), (31, 99))
        self.peer.send_data("shell", session_id, 1, b"stty size; printf '\\377\\n'\n")
        self.peer.wait_bytes(session_id, b"31 99")
        output = self.peer.wait_bytes(session_id, b"\xff")
        self.assertIn(b"\xff", output)
        closed = self.peer.request("shell", "terminal.close", {"session_id": session_id})
        self.assertEqual(closed["status"], "closed")

        source = self.root / "source.bin"
        source.write_bytes(b"abc\x00def")
        read = self.peer.request("fs", "read", {
            "path": str(source), "offset": 0, "length": 32768, "stream_id": "read-stream",
        })
        self.assertEqual(read["bytes"], 7)
        self.peer.wait_event("fs.stream.finished", "read-stream")
        self.assertEqual(bytes(self.peer.data[("read-stream", "data")]), source.read_bytes())

        written = self.root / "written.txt"
        result = self.peer.request("fs", "write", {
            "path": str(written), "data_b64": base64.b64encode(b"one token").decode(),
        })
        self.assertEqual(result["status"], "written")
        replaced = self.peer.request("fs", "replace", {
            "path": str(written), "old": "token", "new": "value",
        })
        self.assertEqual(replaced["status"], "replaced")
        search = self.peer.request("fs", "search", {
            "path": str(written), "query": "value", "limit": 200,
        })
        self.assertEqual(search["results"][0]["line"], 1)
        listing = self.peer.request("fs", "list", {"path": str(self.root), "limit": 200})
        self.assertIn("written.txt", {entry["name"] for entry in listing["entries"]})

        upload_data = os.urandom(4097)
        checksum = hashlib.sha256(upload_data).hexdigest()
        remote = self.root / "uploaded.bin"
        upload = self.peer.request("transfer", "upload.begin", {
            "transfer_id": "upload", "path": str(remote), "size": len(upload_data), "sha256": checksum,
        })
        self.assertEqual(upload["status"], "running")
        for sequence, position in enumerate(range(0, len(upload_data), 512)):
            self.peer.send_data("transfer", "upload", sequence, upload_data[position:position + 512])
        committed = self.peer.request("transfer", "upload.commit", {"transfer_id": "upload"})
        self.assertEqual(committed["status"], "finished")
        self.assertEqual(remote.read_bytes(), upload_data)

        download = self.peer.request("transfer", "download.begin", {
            "transfer_id": "download", "path": str(remote), "checksum": checksum,
        })
        self.assertEqual(download["sha256"], checksum)
        done = self.peer.wait_event("transfer.finished", "download")
        self.assertEqual(done["body"]["status"], "finished")
        self.assertEqual(bytes(self.peer.data[("download", "data")]), upload_data)

        stopped = self.peer.request("control", "agent.stop")
        self.assertEqual(stopped["status"], "stopped")
        self.process.wait(5)
        self.assertEqual(self.process.returncode, 0)

    def test_agent_build_is_deterministic(self) -> None:
        second = self.root / "second.pyz"
        first_meta = build(self.agent)
        second_meta = build(second)
        self.assertEqual(first_meta["sha256"], second_meta["sha256"])
        self.assertEqual(self.agent.read_bytes(), second.read_bytes())

    def test_missing_heartbeat_removes_owned_bootstrap_payload(self) -> None:
        stopped = self.peer.request("control", "agent.stop")
        self.assertEqual(stopped["status"], "stopped")
        self.process.wait(5)
        old_stdout, old_stderr = self.process.communicate()
        self.assertEqual((old_stdout, old_stderr), (b"", b""))
        os.close(self.master)

        bootstrap_dir = Path(tempfile.mkdtemp(prefix="titella-bootstrap-"))
        (bootstrap_dir / "owned-payload").write_text("payload", encoding="utf-8")
        master, slave = pty.openpty()
        self.master = master
        environment = dict(os.environ)
        environment.pop("PYTHONPATH", None)
        self.process = subprocess.Popen(
            [
                self.runtime, str(self.agent), "--port", os.ttyname(slave),
                "--link-id", LINK, "--bootstrap-dir", str(bootstrap_dir),
                "--heartbeat", "1", "--grace", "2",
            ],
            cwd=self.root,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        os.close(slave)
        self.peer = MasterPeer(master)
        hello = self.peer.next_frame(8)
        self.assertEqual(hello["type"], "hello")
        self.assertEqual(self.peer.request("control", "bind", {"link_id": LINK})["status"], "ready")
        self.process.wait(6)
        self.assertEqual(self.process.returncode, 0)
        self.assertFalse(bootstrap_dir.exists())


if __name__ == "__main__":
    unittest.main()
