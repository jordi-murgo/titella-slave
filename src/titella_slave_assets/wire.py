"""LF-delimited JSON protocol v1 shared with the slave agent."""
from __future__ import annotations

import base64
import json
import re
from typing import Any, Dict, Iterable, List, Mapping, Optional, Tuple

PROTOCOL_VERSION = 1
MAX_FRAME_BYTES = 4096
FRAME_BUFFER_BYTES = MAX_FRAME_BYTES + 1
DATA_CHUNK_BYTES = 512
DATA_WINDOW_DEFAULT = 8
DATA_WINDOW_MAX = 64
MAX_REQUESTS = 256
MAX_DEDUP_ENTRIES = 1024
MAX_OUTBOUND_FRAMES = 1024

CHANNELS = frozenset(("hid", "video", "control", "bootstrap", "shell", "fs", "transfer"))
TYPES = frozenset(("hello", "heartbeat", "request", "response", "data", "ack", "event", "error"))
LINK_ID_RE = re.compile(r"^[0-9a-f]{32}$")

MASTER_TO_FIRMWARE = "master_to_firmware"
FIRMWARE_TO_MASTER = "firmware_to_master"
SLAVE_TO_FIRMWARE = "slave_to_firmware"
FIRMWARE_TO_SLAVE = "firmware_to_slave"

_MASTER_TYPES = {
    "control": frozenset(("heartbeat", "request", "ack")),
    "bootstrap": frozenset(("request", "data", "ack")),
    "shell": frozenset(("request", "data", "ack")),
    "fs": frozenset(("request", "data", "ack")),
    "transfer": frozenset(("request", "data", "ack")),
}
_SLAVE_TYPES = {
    "control": frozenset(("hello", "heartbeat", "response", "ack", "event", "error")),
    "bootstrap": frozenset(("response", "ack", "event", "error")),
    "shell": frozenset(("response", "data", "ack", "event", "error")),
    "fs": frozenset(("response", "data", "ack", "event", "error")),
    "transfer": frozenset(("response", "data", "ack", "event", "error")),
}
DIRECTION_TYPES = {
    MASTER_TO_FIRMWARE: {
        "hid": frozenset(("request",)),
        "video": frozenset(("request",)),
        **_MASTER_TYPES,
    },
    FIRMWARE_TO_SLAVE: dict(_MASTER_TYPES),
    SLAVE_TO_FIRMWARE: dict(_SLAVE_TYPES),
    FIRMWARE_TO_MASTER: {
        "hid": frozenset(("response", "error")),
        "video": frozenset(("response", "error")),
        **_SLAVE_TYPES,
    },
}


class WireError(ValueError):
    """Malformed frame or one incompatible with v1."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def _integer(frame: Mapping[str, Any], key: str, minimum: int = 0) -> int:
    value = frame.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise WireError("invalid_frame", f"{key} must be an integer >= {minimum}")
    return value


def validate_frame(frame: Mapping[str, Any], direction: Optional[str] = None) -> Dict[str, Any]:
    if not isinstance(frame, Mapping):
        raise WireError("invalid_frame", "the frame must be a JSON object")
    if frame.get("v") != PROTOCOL_VERSION:
        raise WireError("unsupported_version", "only v=1 is accepted")
    channel = frame.get("channel")
    kind = frame.get("type")
    if channel not in CHANNELS:
        raise WireError("invalid_channel", "unknown channel")
    if kind not in TYPES:
        raise WireError("invalid_type", "unknown type")
    link_id = frame.get("link_id")
    if not isinstance(link_id, str) or not LINK_ID_RE.fullmatch(link_id):
        raise WireError("invalid_link_id", "link_id must hold 128 bits in hexadecimal")

    if direction is not None:
        allowed = DIRECTION_TYPES.get(direction)
        if allowed is None:
            raise WireError("invalid_direction", f"unknown direction: {direction}")
        if kind not in allowed.get(channel, frozenset()):
            raise WireError("direction_denied", f"{channel}.{kind} is not allowed in {direction}")

    if kind in ("request", "response"):
        _integer(frame, "request_id", 1)
        if not isinstance(frame.get("op"), str) or not frame["op"]:
            raise WireError("invalid_frame", "request/response requires op")
    elif kind == "data":
        if not isinstance(frame.get("session_id"), str) or not frame["session_id"]:
            raise WireError("invalid_frame", "data requires session_id")
        _integer(frame, "seq")
        decode_data(frame)
    elif kind == "ack":
        if not isinstance(frame.get("session_id"), str) or not frame["session_id"]:
            raise WireError("invalid_frame", "ack requires session_id")
        _integer(frame, "ack_seq")

    body = frame.get("body")
    if body is not None and not isinstance(body, Mapping):
        raise WireError("invalid_frame", "body must be an object")
    return dict(frame)


def encode_frame(frame: Mapping[str, Any], direction: Optional[str] = None) -> bytes:
    validated = validate_frame(frame, direction)
    try:
        payload = json.dumps(validated, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise WireError("invalid_json", str(exc)) from exc
    if len(payload) > MAX_FRAME_BYTES:
        raise WireError("frame_too_large", f"frame of {len(payload)} bytes; maximum {MAX_FRAME_BYTES}")
    return payload + b"\n"


def decode_line(raw: bytes, direction: Optional[str] = None) -> Dict[str, Any]:
    if len(raw) > MAX_FRAME_BYTES:
        raise WireError("frame_too_large", "frame above the maximum")
    if raw.endswith(b"\r"):
        raw = raw[:-1]
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WireError("invalid_json", str(exc)) from exc
    return validate_frame(value, direction)


def data_b64(data: bytes) -> str:
    if len(data) > DATA_CHUNK_BYTES:
        raise WireError("chunk_too_large", f"chunk of {len(data)} bytes; maximum {DATA_CHUNK_BYTES}")
    return base64.b64encode(data).decode("ascii")


def decode_data(frame: Mapping[str, Any]) -> bytes:
    encoded = frame.get("data_b64")
    if not isinstance(encoded, str):
        raise WireError("invalid_frame", "data requires data_b64")
    try:
        data = base64.b64decode(encoded, validate=True)
    except (ValueError, base64.binascii.Error) as exc:
        raise WireError("invalid_base64", "data_b64 is not valid base64") from exc
    if len(data) > DATA_CHUNK_BYTES:
        raise WireError("chunk_too_large", f"chunk of {len(data)} bytes; maximum {DATA_CHUNK_BYTES}")
    return data


def base_frame(link_id: str, channel: str, kind: str, **fields: Any) -> Dict[str, Any]:
    frame: Dict[str, Any] = {"v": PROTOCOL_VERSION, "channel": channel, "type": kind, "link_id": link_id}
    frame.update(fields)
    return validate_frame(frame)


class FrameParser:
    """Bounded incremental parser; after an overflow it discards up to the next LF."""

    def __init__(self, direction: Optional[str] = None, ignore_invalid: bool = True) -> None:
        self.direction = direction
        self.ignore_invalid = ignore_invalid
        self.buffer = bytearray()
        self.discard_until_lf = False
        self.errors: List[WireError] = []

    def feed(self, chunk: bytes) -> List[Dict[str, Any]]:
        frames: List[Dict[str, Any]] = []
        for byte in chunk:
            if self.discard_until_lf:
                if byte == 0x0A:
                    self.discard_until_lf = False
                    self.buffer.clear()
                continue
            if byte == 0x0A:
                raw = bytes(self.buffer)
                self.buffer.clear()
                if not raw:
                    continue
                try:
                    frames.append(decode_line(raw, self.direction))
                except WireError as exc:
                    self.errors.append(exc)
                    if not self.ignore_invalid:
                        raise
                continue
            if len(self.buffer) >= MAX_FRAME_BYTES:
                error = WireError("frame_too_large", "overflow; discarding up to LF")
                self.errors.append(error)
                self.buffer.clear()
                self.discard_until_lf = True
                if not self.ignore_invalid:
                    raise error
                continue
            self.buffer.append(byte)
        return frames


class Deduplicator:
    """Bounded FIFO window for (link_id, session_id, seq)."""

    def __init__(self, limit: int = MAX_DEDUP_ENTRIES) -> None:
        if limit < 1:
            raise ValueError("limit must be positive")
        self.limit = limit
        self._seen: Dict[Tuple[str, str, int], None] = {}

    def add(self, key: Tuple[str, str, int]) -> bool:
        if key in self._seen:
            return False
        self._seen[key] = None
        if len(self._seen) > self.limit:
            del self._seen[next(iter(self._seen))]
        return True

    def seen(self, key: Tuple[str, str, int]) -> bool:
        """Lookup without registering: the key is registered only after acceptance."""
        return key in self._seen
