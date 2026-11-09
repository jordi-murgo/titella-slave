"""Entry point of the ephemeral slave agent."""
from __future__ import print_function

import argparse
import json
import os
import queue
import re
import shutil
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

from agent_link import AgentLink, AgentLinkError
from file_service import FileService, FileServiceError
from shell_service import JobManager, ProcessRegistry, ShellSessionManager
from transfer_service import TransferError, TransferService
from wire import DATA_WINDOW_DEFAULT, DATA_WINDOW_MAX, Deduplicator, decode_data


def _window_or_default(value):
    """Window requested by the master; any invalid value falls back to 8."""
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= DATA_WINDOW_MAX:
        return DATA_WINDOW_DEFAULT
    return value


class SlaveAgent(object):
    def __init__(self, link_id, port=None, bootstrap_dir=None,
                 heartbeat_s=5.0, grace_s=30.0):
        self.link_id = link_id
        self.bootstrap_dir = Path(bootstrap_dir).resolve() if bootstrap_dir else None
        self.stop_event = threading.Event()
        self.cleanup_lock = threading.Lock()
        self.cleanup_done = threading.Event()
        self.cleaned = False
        self.bound = False
        self.dedup = Deduplicator()
        self.link = AgentLink(
            link_id,
            port=port,
            heartbeat_s=heartbeat_s,
            grace_s=grace_s,
            on_connect=self._hello,
            on_expired=self._expired,
        )
        self.registry = ProcessRegistry(4)
        self.jobs = JobManager(self.link, self.registry)
        self.terminals = ShellSessionManager(self.link, self.registry)
        self.files = FileService()
        self.transfers = TransferService(self.link)

    def _hello(self):
        version = sys.version.split()[0]
        self.link.send_frame({
            "v": 1,
            "channel": "control",
            "type": "hello",
            "link_id": self.link_id,
            "body": {
                "agent": "titella-slave",
                "python": version,
                "pid": os.getpid(),
                "uid": os.getuid(),
                "implementation": "python",
                "runtime": {"name": "python", "version": version},
            },
        })

    def _expired(self):
        # The heartbeat thread must not clean up: interpreter shutdown would
        # interrupt it halfway through. It only signals; the main thread cleans up.
        self.stop_event.set()

    def cleanup(self, remove_payload=True):
        with self.cleanup_lock:
            if self.cleaned:
                self.cleanup_done.wait(30)
                return
            self.cleaned = True
        try:
            self.jobs.shutdown()
            self.terminals.shutdown()
            self.transfers.shutdown()
            self.link.close()
            if remove_payload and self.bootstrap_dir:
                name = self.bootstrap_dir.name
                temporary = Path(tempfile.gettempdir()).resolve()
                if (name.startswith("titella-bootstrap-") or
                        re.match(r"^\.[A-Za-z0-9_]{8}$", name)) and \
                        self.bootstrap_dir.parent == temporary:
                    shutil.rmtree(str(self.bootstrap_dir), ignore_errors=True)
        finally:
            self.cleanup_done.set()

    def run(self):
        self.link.start()
        try:
            while not self.stop_event.is_set() and not self.link.expired:
                try:
                    frame = self.link.inbound.get(timeout=0.25)
                except queue.Empty:
                    continue
                try:
                    self._dispatch(frame)
                finally:
                    self.link.inbound.task_done()
        finally:
            self.cleanup(remove_payload=True)

    def _dispatch(self, frame):
        if frame["type"] == "heartbeat":
            return
        if frame["type"] == "data":
            self._data(frame)
            return
        if frame["type"] != "request":
            return
        try:
            if frame["channel"] == "control":
                self._control(frame)
                return
            if not self.bound:
                attempts = int(frame.get("_bind_attempts", 0))
                if attempts < 20:
                    frame["_bind_attempts"] = attempts + 1
                    self.link.inbound.put(frame)
                    time.sleep(0.05)
                    return
                self.link.send_error(frame, "not_bound", "control.bind is mandatory", True)
                return
            if frame["channel"] == "shell":
                self._shell(frame)
            elif frame["channel"] == "fs":
                self._fs(frame)
            elif frame["channel"] == "transfer":
                self._transfer(frame)
            else:
                self.link.send_error(frame, "unknown_channel", "channel not served", False)
        except FileServiceError as exc:
            self.link.send_error(frame, exc.code, exc.message, False)
        except TransferError as exc:
            self.link.send_error(frame, exc.code, exc.message, exc.code in ("not_found",))
        except (KeyError, ValueError, RuntimeError, OSError) as exc:
            code = str(exc) if str(exc) else "operation_failed"
            self.link.send_error(frame, code, str(exc), False)

    def _control(self, frame):
        body = frame.get("body") or {}
        if frame["op"] == "bind":
            if body.get("link_id") != self.link_id:
                self.link.send_error(frame, "takeover_denied", "link_id does not match", False)
                return
            self.bound = True
            self.link.response(frame, {"status": "ready", "link_id": self.link_id})
        elif frame["op"] == "agent.stop":
            self.link.response(frame, {"status": "stopped"})
            threading.Thread(target=self._stop_after_response, daemon=True).start()
        else:
            self.link.send_error(frame, "unknown_op", "unknown control operation", False)

    def _stop_after_response(self):
        time.sleep(0.25)
        self.cleanup(remove_payload=True)
        self.stop_event.set()

    def _shell(self, frame):
        body = frame.get("body") or {}
        op = frame["op"]
        if op == "job.start":
            result = self.jobs.start(
                body.get("job_id"), body.get("cmd", ""), body.get("timeout", 60), body.get("cwd"),
            )
        elif op == "job.cancel":
            result = self.jobs.cancel(body.get("job_id"))
        elif op == "terminal.open":
            result = self.terminals.open(
                body.get("cwd"), body.get("shell"), body.get("rows", 40),
                body.get("cols", 120), body.get("idle_timeout", 900),
            )
        elif op == "terminal.resize":
            result = self.terminals.resize(body.get("session_id"), body.get("rows", 40), body.get("cols", 120))
        elif op == "terminal.close":
            result = self.terminals.close(body.get("session_id"))
        else:
            raise RuntimeError("unknown_shell_op")
        self.link.response(frame, result)

    def _fs(self, frame):
        body = frame.get("body") or {}
        result, payload = self.files.dispatch(frame["op"], body)
        stream_id = body.get("stream_id") or uuid.uuid4().hex
        if payload is not None:
            result["stream_id"] = stream_id
            result["payload_kind"] = "bytes"
            self.link.response(frame, result)
            self._start_stream("fs", stream_id, payload, "fs.stream.finished")
            return
        encoded = json.dumps(result, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(encoded) > 2048:
            metadata = {"stream_id": stream_id, "payload_kind": "json", "bytes": len(encoded)}
            self.link.response(frame, metadata)
            self._start_stream("fs", stream_id, encoded, "fs.stream.finished")
        else:
            self.link.response(frame, result)

    def _start_stream(self, channel, stream_id, payload, final_op):
        def send():
            try:
                sequence = 0
                for position in range(0, len(payload), 512):
                    self.link.send_data(channel, stream_id, sequence, payload[position:position + 512])
                    sequence += 1
                self.link.event(channel, final_op, stream_id, {"status": "finished", "bytes": len(payload)})
            except BaseException as exc:
                self.link.event(channel, final_op, stream_id, {
                    "status": "error", "code": "stream_failed", "message": str(exc), "retryable": True,
                })
        threading.Thread(target=send, name="fs-stream-%s" % stream_id[:8], daemon=True).start()

    def _transfer(self, frame):
        body = frame.get("body") or {}
        op = frame["op"]
        transfer_id = body.get("transfer_id")
        if op == "upload.begin":
            result = self.transfers.begin_upload(
                transfer_id, body.get("path"), body.get("size"), body.get("sha256"),
            )
        elif op == "upload.commit":
            result = self.transfers.commit_upload(transfer_id)
        elif op == "download.begin":
            result = self.transfers.begin_download(
                transfer_id, body.get("path"), body.get("checksum"),
                window=_window_or_default(body.get("window")),
            )
        elif op == "status":
            result = self.transfers.status(transfer_id)
        elif op == "cancel":
            result = self.transfers.cancel(transfer_id)
        else:
            raise RuntimeError("unknown_transfer_op")
        self.link.response(frame, result)

    def _data(self, frame):
        if not self.bound:
            self.link.send_error(frame, "not_bound", "control.bind is mandatory", True)
            return
        key = (frame["link_id"], frame["session_id"], frame["seq"])
        # The dedup key is recorded only after the frame is accepted. If it were
        # recorded earlier, a rejected gap (no ACK) would be marked as seen and
        # its retransmission would get a fake ACK without applying its bytes.
        if self.dedup.seen(key):
            self.link.ack(frame["channel"], frame["session_id"], frame["seq"])
            return
        try:
            data = decode_data(frame)
            if frame["channel"] == "shell":
                self.terminals.input(frame["session_id"], data)
            elif frame["channel"] == "transfer":
                self.transfers.upload_data(frame["session_id"], frame["seq"], data)
            else:
                raise RuntimeError("data_channel_denied")
        except (KeyError, ValueError, RuntimeError, TransferError) as exc:
            code = getattr(exc, "code", "data_rejected")
            message = getattr(exc, "message", str(exc))
            self.link.send_error(frame, code, message, False)
            return
        self.dedup.add(key)
        self.link.ack(frame["channel"], frame["session_id"], frame["seq"])


def main(argv=None):
    parser = argparse.ArgumentParser(description="Ephemeral agent of Titella")
    parser.add_argument("--port", help="native CDC; auto-detected when omitted")
    parser.add_argument("--link-id", required=True)
    parser.add_argument("--bootstrap-dir")
    parser.add_argument("--heartbeat", type=float, default=5.0)
    parser.add_argument("--grace", type=float, default=30.0)
    args = parser.parse_args(argv)
    if sys.version_info < (3, 9):
        raise SystemExit("python_unavailable: Python 3.9+ is required")
    if len(args.link_id) != 32 or any(char not in "0123456789abcdef" for char in args.link_id):
        raise SystemExit("invalid link_id")
    agent = SlaveAgent(
        args.link_id,
        port=args.port,
        bootstrap_dir=args.bootstrap_dir,
        heartbeat_s=max(1.0, args.heartbeat),
        grace_s=max(args.grace, args.heartbeat),
    )
    agent.run()


if __name__ == "__main__":
    main()
