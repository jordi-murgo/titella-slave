from __future__ import annotations

import hashlib
import os
import tempfile
import threading
import time
import unittest
from pathlib import Path

from implementations.python.file_service import FileService, FileServiceError
from implementations.python.transfer_service import TransferError, TransferService


class FakeLink:
    def __init__(self) -> None:
        self.data: dict[str, bytearray] = {}
        self.events: list[tuple[str, str, str, dict]] = []
        self.condition = threading.Condition()

    def send_data(self, channel, session_id, sequence, data, **extra):
        with self.condition:
            self.data.setdefault(session_id, bytearray()).extend(data)
            self.condition.notify_all()

    def stream_data(self, channel, session_id, read_chunk, window=8, on_ack=None, stop_check=None):
        """Simulated link: acknowledges each block as soon as it is sent."""
        acked = 0
        while True:
            if stop_check is not None and stop_check():
                raise RuntimeError("transfer cancelled")
            chunk = read_chunk()
            if not chunk:
                return acked
            with self.condition:
                self.data.setdefault(session_id, bytearray()).extend(chunk)
                self.condition.notify_all()
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


class FileServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.service = FileService()

    def test_read_and_list_pagination_limits(self) -> None:
        payload = os.urandom(80 * 1024)
        target = self.root / "payload"
        target.write_bytes(payload)
        metadata, page = self.service.read(str(target), 1024, 100000)
        self.assertEqual(len(page), 64 * 1024)
        self.assertEqual(page, payload[1024:1024 + 64 * 1024])
        self.assertEqual(metadata["next_offset"], 1024 + 64 * 1024)
        for index in range(205):
            (self.root / ("entry-%03d" % index)).touch()
        first = self.service.list(str(self.root), 0, 200)
        second = self.service.list(str(self.root), first["next_offset"], 200)
        self.assertEqual(len(first["entries"]), 200)
        self.assertEqual(first["entries"], sorted(first["entries"], key=lambda item: item["name"]))
        self.assertEqual(first["total"], 206)
        self.assertEqual(len(second["entries"]), 6)

    def test_atomic_write_replace_and_delete_boundaries(self) -> None:
        target = self.root / "target.txt"
        result = self.service.write(str(target), b"one token")
        self.assertEqual(result["sha256"], hashlib.sha256(b"one token").hexdigest())
        self.assertFalse(any("titella" in path.name for path in self.root.iterdir()))
        self.service.replace(str(target), "token", "value")
        self.assertEqual(target.read_bytes(), b"one value")
        target.write_text("x x", encoding="utf-8")
        with self.assertRaises(FileServiceError) as raised:
            self.service.replace(str(target), "x", "y")
        self.assertEqual(raised.exception.code, "replace_count")
        with self.assertRaises(FileServiceError):
            self.service.write(str(target), b"x" * (64 * 1024 + 1))
        directory = self.root / "directory"
        directory.mkdir()
        with self.assertRaises(FileServiceError) as raised:
            self.service.delete(str(directory))
        self.assertEqual(raised.exception.code, "directory_delete_denied")
        link = self.root / "link"
        link.symlink_to(directory, target_is_directory=True)
        self.service.delete(str(link))
        self.assertTrue(directory.exists())

    def test_search_and_find_are_capped(self) -> None:
        target = self.root / "lines.txt"
        target.write_text("\n".join("match %d" % index for index in range(300)), encoding="utf-8")
        result = self.service.search(str(target), "match", 1000)
        self.assertEqual(len(result["results"]), 200)
        self.assertTrue(result["truncated"])
        tree = self.root / "tree"
        tree.mkdir()
        for index in range(510):
            (tree / ("item-%03d.log" % index)).touch()
        found = self.service.find(str(tree), "*.log", 1000)
        self.assertEqual(len(found["results"]), 500)
        self.assertTrue(found["truncated"])

    def test_sysinfo_carries_the_runtime_identification(self) -> None:
        info = self.service.sysinfo()
        self.assertEqual(info["runtime"]["name"], "python")
        self.assertEqual(info["runtime"]["version"], info["python"])
        self.assertIsInstance(info["uid"], int)
        self.assertIsInstance(info["gid"], int)


class TransferServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.link = FakeLink()
        self.service = TransferService(self.link)

    def test_upload_is_invisible_until_verified_atomic_commit(self) -> None:
        payload = os.urandom(1200)
        checksum = hashlib.sha256(payload).hexdigest()
        final = self.root / "final.bin"
        self.service.begin_upload("up", str(final), len(payload), checksum)
        self.assertFalse(final.exists())
        for sequence, position in enumerate(range(0, len(payload), 512)):
            self.service.upload_data("up", sequence, payload[position:position + 512])
        result = self.service.commit_upload("up")
        self.assertEqual(result["status"], "finished")
        self.assertEqual(final.read_bytes(), payload)
        self.assertFalse(any("titella" in path.name for path in self.root.iterdir()))

    def test_checksum_failure_and_cancel_remove_partial(self) -> None:
        final = self.root / "bad.bin"
        self.service.begin_upload("bad", str(final), 3, "0" * 64)
        self.service.upload_data("bad", 0, b"bad")
        with self.assertRaises(TransferError) as raised:
            self.service.commit_upload("bad")
        self.assertEqual(raised.exception.code, "checksum_mismatch")
        self.assertFalse(final.exists())
        self.service.begin_upload("cancel", str(final), 10, hashlib.sha256(b"0123456789").hexdigest())
        self.service.upload_data("cancel", 0, b"0123")
        self.assertEqual(self.service.cancel("cancel")["status"], "cancelled")
        self.assertFalse(final.exists())
        self.assertFalse(any("titella" in path.name for path in self.root.iterdir()))

    def test_download_streams_and_rechecks_sha256(self) -> None:
        payload = os.urandom(4097)
        source = self.root / "source.bin"
        source.write_bytes(payload)
        checksum = hashlib.sha256(payload).hexdigest()
        started = self.service.begin_download("down", str(source), checksum)
        self.assertEqual(started["sha256"], checksum)
        finished = self.link.wait_event("transfer.finished", "down")
        self.assertEqual(finished["status"], "finished")
        self.assertEqual(bytes(self.link.data["down"]), payload)


if __name__ == "__main__":
    unittest.main()
