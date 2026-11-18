"""Stdlib-only link of the ephemeral agent.

Three transports share one frame pipeline:

- the native CDC port (default on POSIX);
- the shared desktop clipboard, selected with port ``CLIPBOARD``
  (``agent_protocol = "clipboard"``);
- the HID data collection of the rig, selected with a ``HID:<serial>``
  binding (``slave_transport = "hid"``), which needs no COM port, no clipboard
  and no network on the target and is the only one a bare Windows host can
  use with nothing installed.
"""
from __future__ import print_function

import errno
import glob
import itertools
import os
import queue
import re
import select
import shutil
import subprocess
import threading
import time

# The CDC transport is the only part that needs POSIX termios; a Windows host
# carries the HID transport instead and must still import this module.
try:
    import fcntl
    import termios
except ImportError:  # pragma: no cover - Windows
    fcntl = None
    termios = None

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

_CLIPBOARD_PORT = "CLIPBOARD"


def _is_clipboard_port(port):
    return isinstance(port, str) and port.strip().upper() == _CLIPBOARD_PORT


def is_hid_port(port):
    """True for a ``HID:<12 uppercase hex>`` binding, the USB-only transport."""
    if not isinstance(port, str):
        return False
    value = port.strip()
    if not value.startswith("HID:"):
        return False
    serial = value[4:]
    return len(serial) == 12 and all(c in "0123456789ABCDEF" for c in serial)


class _ClipboardTransport(object):
    """Shared-clipboard CDC for agent_protocol = "clipboard".

    The master writes ``TITELLA_M2S:<link_id>:<seq>:<ack>`` packets to the
    desktop clipboard and the slave answers with ``TITELLA_S2M`` packets that
    carry newline-delimited JSON v1 frames. Exactly one outbound packet is
    unacknowledged at a time; a pure acknowledgement reuses packet sequence 0
    so it never needs an acknowledgement of its own.

    The master sends no idle heartbeats over the clipboard, so :meth:`pump`
    feeds the link a synthetic local heartbeat while it is quiet and drops the
    link's own idle heartbeats before they can touch the user clipboard.
    """

    _ACK_DELAY_S = 0.010
    _REASSERT_S = 0.150
    _HEARTBEAT_S = 2.0
    _MAX_LINES = 16
    _MAX_REASSERTS = 40
    _MAX_LINE_BYTES = 4096
    _MAX_PARTIAL_BYTES = 65536
    _TOOL_TIMEOUT_S = 5.0

    def __init__(self, link_id):
        self._link_id = link_id
        self._lock = threading.Lock()
        self._rx = []
        self._tx_lines = []
        self._partial = b""
        self._last_m2s = 0
        self._next_s2m = 1
        self._inflight = 0
        self._inflight_text = None
        self._inflight_at = 0.0
        self._inflight_attempts = 0
        self._ack_pending = False
        self._ack_pending_at = 0.0
        self._last_text = None
        self._last_heartbeat = time.monotonic()
        self._paste = None
        self._copy = None
        self._closed = False

    def open(self):
        """Resolves the platform clipboard tool; raises when none exists."""
        pairs = []
        paste = shutil.which("pbpaste")
        copy = shutil.which("pbcopy")
        if paste and copy:
            pairs.append(([paste], [copy]))
        paste = shutil.which("wl-paste")
        copy = shutil.which("wl-copy")
        if paste and copy:
            pairs.append(([paste], [copy]))
        xclip = shutil.which("xclip")
        if xclip:
            pairs.append(([xclip, "-selection", "clipboard", "-o"],
                          [xclip, "-selection", "clipboard"]))
        if not pairs:
            raise FileNotFoundError(
                "clipboard_unavailable: pbcopy, wl-copy or xclip is required")
        self._paste, self._copy = pairs[0]

    def close(self):
        with self._lock:
            self._closed = True
            self._tx_lines = []
            self._rx = []

    def write(self, data):
        """Buffers one encoded frame; the reader thread flushes packets."""
        with self._lock:
            if self._closed:
                raise OSError(errno.EIO, "clipboard transport closed")
            if self._partial:
                self._partial += bytes(data)
            else:
                self._partial = bytes(data)
            while True:
                newline = self._partial.find(b"\n")
                if newline < 0:
                    if len(self._partial) > self._MAX_PARTIAL_BYTES:
                        # The wire never splits a line past the frame bound.
                        self._partial = b""
                    return
                line = self._partial[:newline]
                self._partial = self._partial[newline + 1:]
                text = line.decode("utf-8", "replace").rstrip("\r")
                if not text:
                    continue
                # A quiet link must never touch the shared clipboard.
                if '"channel":"control"' in text and '"type":"heartbeat"' in text:
                    continue
                self._tx_lines.append(text)

    def pump(self):
        """One poll cycle: read, acknowledge, flush; returns frame bytes."""
        text = self._read_clipboard()
        now = time.monotonic()
        with self._lock:
            if self._closed:
                return b""
            self._consume(text, now)
            self._reassert(now)
            self._flush(now)
            if self._rx:
                chunk = b"".join(self._rx)
                self._rx = []
                self._last_heartbeat = now
                return chunk
            if now - self._last_heartbeat >= self._HEARTBEAT_S:
                self._last_heartbeat = now
                return ('{"v":1,"channel":"control","type":"heartbeat",'
                        '"link_id":"%s"}\n' % self._link_id).encode("utf-8")
            return b""

    def _consume(self, text, now):
        if text is None or text == self._last_text:
            return
        self._last_text = text
        prefix = "TITELLA_M2S:" + self._link_id + ":"
        if not text.startswith(prefix):
            return
        head, _, body = text.partition("\n")
        parts = head.strip().split(":")
        if len(parts) != 4:
            return
        try:
            m2s = int(parts[2])
            ack = int(parts[3])
        except ValueError:
            return
        if self._inflight and ack >= self._inflight:
            self._inflight = 0
            self._inflight_text = None
            self._inflight_attempts = 0
        if m2s > self._last_m2s:
            self._last_m2s = m2s
            for raw in body.split("\n"):
                line = raw.rstrip("\r")
                if not line:
                    continue
                payload = line.encode("utf-8") + b"\n"
                if len(payload) > self._MAX_LINE_BYTES + 1:
                    continue
                self._rx.append(payload)
            self._ack_pending = True
            self._ack_pending_at = now
        elif m2s == self._last_m2s and m2s > 0:
            # A re-asserted duplicate: the payload is already buffered, but
            # the peer still needs the acknowledgement it missed.
            self._ack_pending = True
            self._ack_pending_at = now

    def _reassert(self, now):
        if not self._inflight or now - self._inflight_at < self._REASSERT_S:
            return
        if self._last_text == self._inflight_text:
            return
        if self._inflight_attempts >= self._MAX_REASSERTS:
            # The peer stopped consuming packets: hand the block back to the
            # link (its own ACK timeout retries) instead of overwriting the
            # user clipboard forever.
            self._inflight = 0
            self._inflight_text = None
            self._inflight_attempts = 0
            return
        if self._write_clipboard(self._inflight_text):
            self._inflight_attempts += 1
            self._inflight_at = now
            self._last_text = self._inflight_text

    def _flush(self, now):
        if not self._inflight and self._tx_lines:
            take = self._tx_lines[:self._MAX_LINES]
            sequence = self._next_s2m
            packet = ("TITELLA_S2M:%s:%d:%d\n"
                      % (self._link_id, sequence, self._last_m2s)
                      + "\n".join(take) + "\n")
            if self._write_clipboard(packet):
                del self._tx_lines[:self._MAX_LINES]
                self._next_s2m = sequence + 1
                self._inflight = sequence
                self._inflight_text = packet
                self._inflight_at = now
                self._inflight_attempts = 0
                # The packet header already carries the acknowledgement.
                self._ack_pending = False
                self._last_text = packet
        elif self._ack_pending and now - self._ack_pending_at >= self._ACK_DELAY_S:
            # Delay the pure acknowledgement briefly so responses produced by
            # the dispatch thread ride the same packet.
            packet = "TITELLA_S2M:%s:0:%d\n" % (self._link_id, self._last_m2s)
            if self._write_clipboard(packet):
                self._ack_pending = False
                self._last_text = packet

    def _read_clipboard(self):
        try:
            completed = subprocess.run(
                self._paste,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                timeout=self._TOOL_TIMEOUT_S,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            return None
        return completed.stdout.decode("utf-8", "replace")

    def _write_clipboard(self, text):
        try:
            completed = subprocess.run(
                self._copy,
                input=text.encode("utf-8"),
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=self._TOOL_TIMEOUT_S,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            return False
        return completed.returncode == 0


class AgentLinkError(RuntimeError):
    pass


class AgentLink(object):
    def __init__(self, link_id, port=None, heartbeat_s=5.0, grace_s=30.0,
                 on_connect=None, on_expired=None):
        self.link_id = link_id
        self.port_arg = port
        self.clipboard = _is_clipboard_port(port)
        self.hid = is_hid_port(port)
        self._cb = _ClipboardTransport(link_id) if self.clipboard else None
        self._hs = None
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
        if self._connected.is_set() and (
                self._fd is not None or self._cb is not None or self._hs is not None):
            return True
        return False

    def start(self):
        if self._threads:
            return
        if self.clipboard:
            reader = self._clipboard_reader_loop
        elif self.hid:
            reader = self._hid_reader_loop
        else:
            reader = self._reader_loop
        self._threads = [
            threading.Thread(target=reader, name="agent-cdc-reader", daemon=True),
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
            clipboard, self._cb = self._cb, None
            hid, self._hs = self._hs, None
            self._connected.clear()
        if fd is not None:
            try:
                os.close(fd)
            except OSError:
                pass
        if clipboard is not None:
            clipboard.close()
        if hid is not None:
            try:
                hid.close()
            except OSError:
                pass

    def _lost(self):
        if self.disconnected_since is None:
            self.disconnected_since = time.monotonic()
        self._close_fd()

    def _ingest(self, chunk):
        """Feeds decoded bytes to the parser and routes every matching frame."""
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
                self._ingest(chunk)
            except (OSError, ValueError):
                self._lost()

    def _hid_reader_loop(self):
        """HID counterpart of the CDC reader: open the collection, then read."""
        attempt = 0
        while not self._stop.is_set():
            try:
                import hid_transport
                stream = hid_transport.open_stream(self.port_arg.strip())
            except (OSError, ValueError):
                delay = _BACKOFF[min(attempt, len(_BACKOFF) - 1)]
                attempt += 1
                self._stop.wait(delay)
                continue
            with self._fd_lock:
                self._hs = stream
                self.port = self.port_arg.strip()
                self._parser = FrameParser(FIRMWARE_TO_SLAVE, ignore_invalid=True)
                self._connected.set()
            if self.on_connect:
                self.on_connect()
            while not self._stop.is_set():
                try:
                    chunk = stream.read(4096)
                except (OSError, ValueError):
                    break
                if not chunk:
                    continue
                self._ingest(chunk)
            with self._fd_lock:
                hid, self._hs = self._hs, None
                self._connected.clear()
            if hid is not None:
                try:
                    hid.close()
                except OSError:
                    pass
            if self._stop.is_set():
                return
            self.disconnected_since = self.disconnected_since or time.monotonic()
            attempt += 1
            self._stop.wait(_BACKOFF[min(attempt, len(_BACKOFF) - 1)])

    def _clipboard_reader_loop(self):
        """Clipboard counterpart of the CDC reader: poll, ingest, repeat."""
        transport = self._cb
        if transport is None:
            return
        attempt = 0
        while not self._stop.is_set():
            try:
                transport.open()
                break
            except (OSError, ValueError):
                delay = _BACKOFF[min(attempt, len(_BACKOFF) - 1)]
                attempt += 1
                self._stop.wait(delay)
        if self._stop.is_set():
            return
        with self._fd_lock:
            self.port = self.port_arg
            self._parser = FrameParser(FIRMWARE_TO_SLAVE, ignore_invalid=True)
            self._connected.set()
        if self.on_connect:
            self.on_connect()
        while not self._stop.is_set():
            try:
                chunk = transport.pump()
            except (OSError, ValueError):
                self._stop.wait(_BACKOFF[0])
                continue
            if not chunk:
                self._stop.wait(0.02)
                continue
            self._ingest(chunk)

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
                if self._cb is not None:
                    try:
                        self._cb.write(payload)
                    except OSError:
                        # A closed channel is the teardown path, not a loss.
                        pass
                    continue
                if self.hid:
                    # Same discipline as the CDC: wait for a bound stream
                    # instead of dropping the frame. Write returns once the
                    # peer acknowledged the bytes, so the stream, not this
                    # loop, owns retransmission.
                    while not self._stop.is_set():
                        if not self._connected.wait(0.25):
                            continue
                        with self._fd_lock:
                            stream = self._hs
                        if stream is None:
                            continue
                        try:
                            stream.write(payload)
                            break
                        except (OSError, ValueError):
                            self._lost()
                    continue
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
