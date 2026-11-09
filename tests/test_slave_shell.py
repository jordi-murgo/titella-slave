from __future__ import annotations

import os
import tempfile
import threading
import time
import unittest
from pathlib import Path

from implementations.python.shell_service import (
    JobManager,
    ProcessRegistry,
    ShellSessionManager,
)


class CaptureLink:
    def __init__(self, block: bool = False) -> None:
        self.output: dict[str, bytearray] = {}
        self.events: list[tuple[str, str, str, dict]] = []
        self.condition = threading.Condition()
        self.release = threading.Event()
        if not block:
            self.release.set()

    def send_data(self, channel, session_id, sequence, data, **extra):
        self.release.wait(10)
        with self.condition:
            self.output.setdefault(session_id, bytearray()).extend(data)
            self.condition.notify_all()

    def event(self, channel, op, session_id, body):
        with self.condition:
            self.events.append((channel, op, session_id, body))
            self.condition.notify_all()

    def wait_event(self, op: str, session_id: str, timeout: float = 8) -> dict:
        deadline = time.monotonic() + timeout
        with self.condition:
            while time.monotonic() < deadline:
                for event in self.events:
                    if event[1] == op and event[2] == session_id:
                        return event[3]
                self.condition.wait(deadline - time.monotonic())
        raise TimeoutError(op)


class SlaveShellTests(unittest.TestCase):
    def test_job_timeout_and_cancel_kill_process_groups(self) -> None:
        link = CaptureLink()
        registry = ProcessRegistry(4)
        jobs = JobManager(link, registry)
        jobs.start("timeout", "sleep 30", timeout=1)
        timed_out = link.wait_event("job.finished", "timeout", timeout=5)
        self.assertTrue(timed_out["timed_out"])
        self.assertNotEqual(timed_out["returncode"], 0)

        with tempfile.TemporaryDirectory() as temp:
            pid_file = Path(temp) / "child.pid"
            jobs.start("cancel", "sleep 30 & echo $! > %s; wait" % pid_file, timeout=60)
            deadline = time.monotonic() + 5
            while not pid_file.exists() and time.monotonic() < deadline:
                time.sleep(0.02)
            child_pid = int(pid_file.read_text())
            self.assertEqual(jobs.cancel("cancel")["status"], "cancelled")
            cancelled = link.wait_event("job.finished", "cancel", timeout=5)
            self.assertTrue(cancelled["cancelled"])
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                try:
                    os.kill(child_pid, 0)
                except ProcessLookupError:
                    break
                time.sleep(0.05)
            else:
                self.fail("the descendant survived the cancellation of the group")
        jobs.shutdown()
        self.assertEqual(registry.count, 0)

    def test_total_process_limit_is_shared(self) -> None:
        link = CaptureLink()
        registry = ProcessRegistry(4)
        jobs = JobManager(link, registry)
        for index in range(4):
            jobs.start("job-%d" % index, "sleep 30", timeout=60)
        with self.assertRaisesRegex(RuntimeError, "session_limit"):
            jobs.start("fifth", "true", timeout=5)
        jobs.shutdown()
        deadline = time.monotonic() + 5
        while registry.count and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertEqual(registry.count, 0)

    def test_pty_queue_caps_at_256_kib_and_applies_backpressure(self) -> None:
        link = CaptureLink(block=True)
        registry = ProcessRegistry(4)
        terminals = ShellSessionManager(link, registry)
        terminal_info = terminals.open(rows=24, cols=80, idle_timeout=30)
        session_id = terminal_info["session_id"]
        terminal = terminals.sessions[session_id]
        terminals.input(
            session_id,
            b"python3 -c 'import os; os.write(1, b\"x\" * 400000)'\n",
        )
        deadline = time.monotonic() + 8
        while terminal.queue.qsize() < 512 and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertEqual(terminal.queue.maxsize * 512, 256 * 1024)
        self.assertEqual(terminal.queue.qsize(), terminal.queue.maxsize)
        link.release.set()
        terminals.close(session_id)
        terminals.shutdown()
        deadline = time.monotonic() + 5
        while registry.count and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertEqual(registry.count, 0)

    def test_terminal_idle_timeout_closes_session(self) -> None:
        link = CaptureLink()
        registry = ProcessRegistry(4)
        terminals = ShellSessionManager(link, registry)
        session = terminals.open(idle_timeout=1)
        event = link.wait_event("terminal.closed", session["session_id"], timeout=5)
        self.assertEqual(event["status"], "closed")
        terminals.shutdown()


if __name__ == "__main__":
    unittest.main()
