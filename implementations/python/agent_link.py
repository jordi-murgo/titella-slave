"""Stdlib-only CDC link of the ephemeral agent."""
from __future__ import print_function

import errno
import fcntl
import glob
import itertools
import os
import queue
import re
import select
import subprocess
import termios
import threading
import time

from wire import (
    DATA_CHUNK_BYTES,
    DATA_WINDOW_DEFAULT,
    DATA_WINDOW_MAX,
    FIRMWARE_TO_SLAVE,
    SLAVE_TO_FIRMWARE,
    FrameParser,
    base_frame,
    data_b64,
    encode_frame,
)

_ACK_TIMEOUT_S = 1.0
_ACK_RETRIES = 3

_SERIAL_RE = re.compile(r"/dev/cu\.usbmodem[^\s\"']+")
_BACKOFF = (0.5, 1.0, 2.0, 5.0)


class AgentLinkError(RuntimeError):
    pass


class AgentLink(object):
    def __init__(self, link_id, port=None, heartbeat_s=5.0, grace_s=30.0,
                 on_connect=None, on_expired=None):
        self.link_id = link_id
        self.port_arg = port
        self.heartbeat_s = heartbeat_s
        self.grace_s = grace_s
        self.on_connect = on_connect
        self.on_expired = on_expired
        self.inbound = queue.Queue(maxsize=1024)
        self._outbound = queue.PriorityQueue(maxsize=1024)
        self._order = itertools.count()
        self._fd = None
        self._fd_lock = threading.Lock()
        self._connected = threading.Event()
        self._stop = threading.Event()
        self._parser = FrameParser(FIRMWARE_TO_SLAVE, ignore_invalid=True)
        self._acks = {}
        self._acks_lock = threading.Lock()
        self._ack_cond = threading.Condition(self._acks_lock)
        self._data_lock = threading.Lock()
        self._threads = []
        self.last_seen = time.monotonic()
        self.disconnected_since = None
        self.expired = False

    @property
    def connected(self):
        return self._connected.is_set() and self._fd is not None

    def start(self):
        if self._threads:
            return
        self._threads = [
            threading.Thread(target=self._reader_loop, name="agent-cdc-reader", daemon=True),
            threading.Thread(target=self._writer_loop, name="agent-cdc-writer", daemon=True),
            threading.Thread(target=self._heartbeat_loop, name="agent-heartbeat", daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    def close(self):
        self._stop.set()
        self._connected.set()
        self._close_fd()
        with self._acks_lock:
            self._acks.clear()
            self._ack_cond.notify_all()
        for thread in self._threads:
            if thread is not threading.current_thread():
                thread.join(timeout=2)

    def _candidates(self):
        if self.port_arg:
            return [self.port_arg]
        preferred = []
        try:
            completed = subprocess.run(
                ["/usr/sbin/ioreg", "-r", "-c", "IOSerialBSDClient", "-l"],
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                timeout=3,
                check=False,
            )
            # Only the Titella device is preferred; a stale product string is
            # not accepted as a second identity.
            if "Titella KVM" in completed.stdout:
                preferred = _SERIAL_RE.findall(completed.stdout)
        except (OSError, subprocess.TimeoutExpired):
            pass
        fallback = sorted(glob.glob("/dev/cu.usbmodem*"))
        return preferred + [path for path in fallback if path not in preferred]

    def _open(self):
        last_error = None
        for port in self._candidates():
            try:
                fd = os.open(port, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
                try:
                    if hasattr(termios, "TIOCEXCL"):
                        fcntl.ioctl(fd, termios.TIOCEXCL)
                    attrs = termios.tcgetattr(fd)
                    attrs[0] = 0
                    attrs[1] = 0
                    attrs[2] = termios.CS8 | termios.CREAD | termios.CLOCAL
                    attrs[3] = 0
                    attrs[4] = termios.B115200
                    attrs[5] = termios.B115200
                    attrs[6][termios.VMIN] = 0
                    attrs[6][termios.VTIME] = 0
                    termios.tcsetattr(fd, termios.TCSANOW, attrs)
                except BaseException:
                    os.close(fd)
                    raise
                with self._fd_lock:
                    self._fd = fd
                    self.port = port
                    self._parser = FrameParser(FIRMWARE_TO_SLAVE, ignore_invalid=True)
                    self._connected.set()
                if self.on_connect:
                    self.on_connect()
                return
            except OSError as exc:
                last_error = exc
        if last_error:
            raise last_error
        raise FileNotFoundError("the Titella CDC has not been found")

    def _close_fd(self):
        with self._fd_lock:
            fd, self._fd = self._fd, None
            self._connected.clear()
        if fd is not None:
            try:
                os.close(fd)
            except OSError:
                pass

    def _lost(self):
        if self.disconnected_since is None:
            self.disconnected_since = time.monotonic()
        self._close_fd()

    def _reader_loop(self):
        attempt = 0
        while not self._stop.is_set():
            if self._fd is None:
                try:
                    self._open()
                    attempt = 0
                except (OSError, ValueError):
                    delay = _BACKOFF[min(attempt, len(_BACKOFF) - 1)]
                    attempt += 1
                    self._stop.wait(delay)
                    continue
            with self._fd_lock:
                fd = self._fd
            if fd is None:
                continue
            try:
                readable, _, _ = select.select([fd], [], [], 0.25)
                if not readable:
                    continue
                chunk = os.read(fd, 4096)
                if not chunk:
                    raise OSError(errno.EIO, "CDC EOF")
                for frame in self._parser.feed(chunk):
                    if frame.get("link_id") != self.link_id:
                        continue
                    self.last_seen = time.monotonic()
                    self.disconnected_since = None
                    if frame["type"] == "ack":
                        key = (frame["session_id"], frame["ack_seq"])
                        with self._acks_lock:
                            event = self._acks.get(key)
                            if event:
                                event.set()
                            self._ack_cond.notify_all()
                    else:
                        try:
                            self.inbound.put(frame, timeout=1)
                        except queue.Full:
                            self.send_error(frame, "queue_full", "inbound queue full", True)
            except (OSError, ValueError):
                self._lost()

    def _priority(self, frame):
        if frame["type"] in ("ack", "heartbeat"):
            return 0
        if frame["type"] in ("hello", "response", "error", "event"):
            return 1
        if frame["channel"] == "shell":
            return 2
        return 3

    def send_frame(self, frame):
        if self._stop.is_set():
            raise AgentLinkError("link closed")
        encode_frame(frame, SLAVE_TO_FIRMWARE)
        try:
            self._outbound.put((self._priority(frame), next(self._order), frame), timeout=1)
        except queue.Full:
            raise AgentLinkError("outbound queue full")

    def _writer_loop(self):
        while not self._stop.is_set():
            try:
                _, _, frame = self._outbound.get(timeout=0.25)
            except queue.Empty:
                continue
            try:
                payload = encode_frame(frame, SLAVE_TO_FIRMWARE)
                while not self._stop.is_set():
                    if not self._connected.wait(0.25):
                        continue
                    with self._fd_lock:
                        fd = self._fd
                    if fd is None:
                        continue
                    sent = 0
                    try:
                        while sent < len(payload):
                            _, writable, _ = select.select([], [fd], [], 1)
                            if not writable:
                                raise OSError(errno.EIO, "timeout writing to CDC")
                            count = os.write(fd, payload[sent:])
                            if count <= 0:
                                raise OSError(errno.EIO, "short write CDC")
                            sent += count
                        break
                    except OSError:
                        self._lost()
            finally:
                self._outbound.task_done()

    def _heartbeat_loop(self):
        while not self._stop.wait(self.heartbeat_s):
            if self.connected:
                try:
                    self.send_frame(base_frame(self.link_id, "control", "heartbeat"))
                except Exception:
                    pass
            origin = self.disconnected_since or self.last_seen
            if origin and time.monotonic() - origin > self.grace_s and not self.expired:
                self.expired = True
                if self.on_expired:
                    self.on_expired()
                self._stop.set()
                self._close_fd()
                return

    def response(self, request, body):
        self.send_frame(base_frame(
            self.link_id,
            request["channel"],
            "response",
            request_id=request["request_id"],
            op=request["op"],
            body=body,
        ))

    def send_error(self, request, code, message, retryable=False):
        fields = {"body": {"code": code, "message": message, "retryable": bool(retryable)}}
        if isinstance(request.get("request_id"), int):
            fields["request_id"] = request["request_id"]
            fields["op"] = request.get("op", "unknown")
        self.send_frame(base_frame(self.link_id, request.get("channel", "control"), "error", **fields))

    def event(self, channel, op, session_id, body):
        self.send_frame(base_frame(
            self.link_id, channel, "event", op=op, session_id=session_id, body=body,
        ))

    def ack(self, channel, session_id, sequence):
        self.send_frame(base_frame(
            self.link_id, channel, "ack", session_id=session_id, ack_seq=sequence,
        ))

    def send_data(self, channel, session_id, sequence, data, **extra):
        frame = base_frame(
            self.link_id,
            channel,
            "data",
            session_id=session_id,
            seq=sequence,
            data_b64=data_b64(data),
            **extra
        )
        key = (session_id, sequence)
        with self._data_lock:
            for _ in range(4):
                event = threading.Event()
                with self._acks_lock:
                    self._acks[key] = event
                self.send_frame(frame)
                if event.wait(1):
                    with self._acks_lock:
                        self._acks.pop(key, None)
                    return
                with self._acks_lock:
                    self._acks.pop(key, None)
            raise AgentLinkError("ACK timeout for %s:%d" % key)

    def stream_data(self, channel, session_id, read_chunk, window=DATA_WINDOW_DEFAULT, on_ack=None, stop_check=None):
        """Sliding window with per-chunk ACK for a data stream.

        ``read_chunk`` returns the next block (<=512 bytes) or ``b""`` at the
        end. It is only read when there is room in the window, so read-ahead is
        bounded to ``window`` blocks. Expired blocks are retransmitted in
        sequence order and are abandoned after ``_ACK_RETRIES`` retransmissions.
        It does not take ``_data_lock``: a file does not block interactive
        sends.
        """
        if window < 1 or window > DATA_WINDOW_MAX:
            raise ValueError("window out of range")
        pending = {}
        next_seq = 0
        eof = False
        acked_bytes = 0
        try:
            while pending or not eof:
                if self._stop.is_set():
                    raise AgentLinkError("link closed")
                if stop_check is not None and stop_check():
                    raise AgentLinkError("transfer cancelled")
                while not eof and len(pending) < window:
                    chunk = read_chunk()
                    if not chunk:
                        eof = True
                        break
                    if len(chunk) > DATA_CHUNK_BYTES:
                        raise AgentLinkError("chunk above 512 bytes")
                    seq = next_seq
                    next_seq += 1
                    event = threading.Event()
                    with self._acks_lock:
                        self._acks[(session_id, seq)] = event
                    pending[seq] = [chunk, event, 0, time.monotonic() + _ACK_TIMEOUT_S]
                    self.send_frame(base_frame(
                        self.link_id,
                        channel,
                        "data",
                        session_id=session_id,
                        seq=seq,
                        data_b64=data_b64(chunk),
                    ))
                if not pending:
                    break
                with self._ack_cond:
                    while True:
                        if self._stop.is_set():
                            break
                        if any(entry[1].is_set() for entry in pending.values()):
                            break
                        remaining = min(entry[3] for entry in pending.values()) - time.monotonic()
                        if remaining <= 0:
                            break
                        self._ack_cond.wait(remaining)
                now = time.monotonic()
                for seq in [item for item in pending if pending[item][1].is_set()]:
                    data = pending.pop(seq)[0]
                    with self._acks_lock:
                        self._acks.pop((session_id, seq), None)
                    acked_bytes += len(data)
                    if on_ack is not None:
                        on_ack(acked_bytes)
                for seq in sorted(item for item in pending if now >= pending[item][3]):
                    data, event, attempts, _deadline = pending[seq]
                    if attempts >= _ACK_RETRIES:
                        raise AgentLinkError("ACK timeout for %s:%d" % ((session_id, seq)))
                    pending[seq][2] = attempts + 1
                    pending[seq][3] = now + _ACK_TIMEOUT_S
                    self.send_frame(base_frame(
                        self.link_id,
                        channel,
                        "data",
                        session_id=session_id,
                        seq=seq,
                        data_b64=data_b64(data),
                    ))
            return acked_bytes
        finally:
            with self._acks_lock:
                for seq in list(pending):
                    self._acks.pop((session_id, seq), None)
