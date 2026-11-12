"""Loading and validation of the slave bundles shipped inside the wheel.

The wheel carries the generated artifacts under ``_data/``: the deterministic
Python zipapp, the Windows C# source, both loader templates and a manifest
pinning size and SHA-256 of each file. ``load_bundle`` reads them with
``importlib.resources`` — never from a sibling checkout or a path supplied by
the caller — and refuses to serve a bundle that does not match its manifest.
"""
from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from importlib.resources import files as _resource_files
from typing import Any

from titella_slave_assets import __version__
from titella_slave_assets.wire import PROTOCOL_VERSION as _WIRE_PROTOCOL_VERSION

# Manifest contract; must stay in lockstep with build_support.py. The
# protocol version is not re-declared here: it comes from the canonical wire
# codec, so the bundle API can never disagree with the frames it validates.
SCHEMA_VERSION = 2
PROTOCOL_VERSION = _WIRE_PROTOCOL_VERSION
IMPLEMENTATIONS = ("python", "windows-dotnet")
MAX_MANIFEST_BYTES = 64 * 1024
MAX_LOADER_BYTES = 512 * 1024
# The Windows HID bootstrap artifacts are bounded before they reach the
# loader renderer; both are refused rather than truncated above this size.
MAX_HID_ARTIFACT_BYTES = 64 * 1024
HID_BOOTSTRAP_KEYS = ("stage0", "stage0_sha256", "transport", "transport_sha256")

# Public error codes carried by BundleError.code. The plan fixes this exact
# vocabulary: missing artifact, corrupted/invalid bundle, protocol drift.
BUNDLE_MISSING = "slave_bundle_missing"
BUNDLE_INVALID = "slave_bundle_invalid"
BUNDLE_INCOMPATIBLE = "slave_bundle_incompatible"

_CODE_MISSING = BUNDLE_MISSING
_CODE_INVALID = BUNDLE_INVALID
_CODE_INCOMPATIBLE = BUNDLE_INCOMPATIBLE


class BundleError(RuntimeError):
    """A slave bundle cannot be served; no download, rebuild or fallback."""

    def __init__(self, code: str, message: str) -> None:
        RuntimeError.__init__(self, message)
        self.code = code
        self.message = message


@dataclass(frozen=True)
class SlaveBundle:
    """One validated implementation of the slave payload contract."""

    implementation: str
    protocol: int
    filename: str
    payload: bytes
    loader_source: str
    sha256: str
    # Windows-only: the two artifacts of the USB-only HID bootstrap. Python
    # has no HID transport and keeps both fields None.
    hid_stage0_source: str | None = None
    hid_transport_source: str | None = None


def _data_resource() -> Any:
    """The ``_data`` directory of the installed wheel; never a checkout path."""
    return _resource_files("titella_slave_assets").joinpath("_data")


def _safe_member(name: Any) -> str:
    """Manifest artifact names must be plain files inside ``_data``.

    Absolute paths, directory separators, traversal, drive letters and
    home-relative names are rejected before anything is opened.
    """
    if not isinstance(name, str) or not name:
        raise BundleError(_CODE_INVALID, "the manifest names an empty artifact")
    if name != name.strip():
        raise BundleError(_CODE_INVALID, f"artifact name is not trimmed: {name!r}")
    if "/" in name or "\\" in name:
        raise BundleError(_CODE_INVALID, f"artifact name has a separator: {name!r}")
    if name in {".", ".."} or name.startswith("~"):
        raise BundleError(_CODE_INVALID, f"artifact name is not a plain file: {name!r}")
    if ":" in name:
        raise BundleError(_CODE_INVALID, f"artifact name looks like a path: {name!r}")
    if any(ord(char) < 32 or ord(char) == 127 for char in name):
        raise BundleError(_CODE_INVALID, f"artifact name has control characters: {name!r}")
    return name


def _load_manifest(data_dir: Any) -> dict[str, Any]:
    manifest_path = data_dir.joinpath("manifest.json")
    if not manifest_path.is_file():
        raise BundleError(
            _CODE_MISSING, "the installed assets wheel carries no manifest.json"
        )
    raw = manifest_path.read_bytes()
    if not raw:
        raise BundleError(_CODE_INVALID, "the installed manifest is empty")
    if len(raw) > MAX_MANIFEST_BYTES:
        raise BundleError(_CODE_INVALID, "the installed manifest exceeds its size bound")
    try:
        manifest = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise BundleError(
            _CODE_INVALID, f"the installed manifest is not valid JSON: {exc}"
        ) from exc
    if not isinstance(manifest, dict):
        raise BundleError(_CODE_INVALID, "the installed manifest is not an object")
    return manifest


def _validate_manifest(manifest: dict[str, Any]) -> dict[str, Any]:
    """Checks the manifest header; returns the implementations table."""
    if manifest.get("schema_version") != SCHEMA_VERSION:
        raise BundleError(
            _CODE_INCOMPATIBLE,
            "the installed manifest has an unsupported schema_version: %r"
            % (manifest.get("schema_version"),),
        )
    if manifest.get("protocol") != PROTOCOL_VERSION:
        raise BundleError(
            _CODE_INCOMPATIBLE,
            "the installed assets speak protocol %r; this master requires %d"
            % (manifest.get("protocol"), PROTOCOL_VERSION),
        )
    version = manifest.get("version")
    if version != __version__:
        raise BundleError(
            _CODE_INCOMPATIBLE,
            "the installed manifest declares version %r while the installed "
            "titella_slave_assets package is %r" % (version, __version__),
        )
    implementations = manifest.get("implementations")
    if not isinstance(implementations, dict) or not implementations:
        raise BundleError(_CODE_INVALID, "the manifest carries no implementations")
    return implementations


def _validate_entry(implementation: str, entry: Any) -> dict[str, Any]:
    if not isinstance(entry, dict):
        raise BundleError(
            _CODE_INVALID, f"the manifest entry for {implementation!r} is not an object"
        )
    for key in ("payload", "loader", "size", "sha256", "loader_sha256"):
        if key not in entry:
            raise BundleError(
                _CODE_INVALID, f"the manifest entry for {implementation!r} omits {key}"
            )
    size = entry["size"]
    if isinstance(size, bool) or not isinstance(size, int) or size < 0:
        raise BundleError(_CODE_INVALID, "a manifest entry has an invalid size")
    for key in ("sha256", "loader_sha256"):
        value = entry[key]
        if not isinstance(value, str) or len(value) != 64:
            raise BundleError(_CODE_INVALID, f"a manifest entry has an invalid {key}")
    return entry


def _read_artifact(data_dir: Any, name: str) -> bytes:
    path = data_dir.joinpath(_safe_member(name))
    if not path.is_file():
        raise BundleError(_CODE_MISSING, f"the installed assets lack {name}")
    return path.read_bytes()


def _validate_hid_bootstrap(implementation: str, entry: dict[str, Any]) -> dict[str, Any]:
    """The Windows entry must carry exactly the four HID bootstrap keys."""
    if "hid_bootstrap" not in entry:
        raise BundleError(
            _CODE_INVALID,
            f"the manifest entry for {implementation!r} omits hid_bootstrap",
        )
    bootstrap = entry["hid_bootstrap"]
    if not isinstance(bootstrap, dict):
        raise BundleError(_CODE_INVALID, "the hid_bootstrap block is not an object")
    if set(bootstrap) != set(HID_BOOTSTRAP_KEYS):
        raise BundleError(
            _CODE_INVALID,
            "the hid_bootstrap block must carry exactly "
            + ", ".join(HID_BOOTSTRAP_KEYS),
        )
    for key in ("stage0_sha256", "transport_sha256"):
        value = bootstrap[key]
        if not isinstance(value, str) or len(value) != 64:
            raise BundleError(_CODE_INVALID, f"hid_bootstrap has an invalid {key}")
    return bootstrap


def _read_hid_artifact(data_dir: Any, name: Any, expected_sha256: str,
                       label: str) -> str:
    """Reads one bounded HID artifact and returns its validated source text."""
    member = _safe_member(name)
    path = data_dir.joinpath(member)
    if not path.is_file():
        raise BundleError(_CODE_MISSING, f"the installed assets lack {label} {member}")
    data = path.read_bytes()
    if len(data) > MAX_HID_ARTIFACT_BYTES:
        raise BundleError(
            _CODE_INVALID,
            f"{label} {member} exceeds the {MAX_HID_ARTIFACT_BYTES}-byte bound",
        )
    if hashlib.sha256(data).hexdigest() != expected_sha256:
        raise BundleError(_CODE_INVALID, f"{label} {member} does not match its manifest SHA-256")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise BundleError(_CODE_INVALID, f"{label} {member} is not valid UTF-8: {exc}") from exc


def _load_from(data_dir: Any, implementation: str) -> SlaveBundle:
    """Validates and loads one implementation from a ``Traversable`` data dir.

    The public entry point resolves the directory with importlib.resources;
    this core takes it as a parameter so the validation itself can be tested
    against synthetic data directories without building a wheel.
    """
    manifest = _load_manifest(data_dir)
    implementations = _validate_manifest(manifest)
    if implementation not in IMPLEMENTATIONS:
        raise BundleError(
            _CODE_INVALID,
            f"unknown slave implementation {implementation!r}; expected one of "
            + ", ".join(IMPLEMENTATIONS),
        )
    if implementation not in implementations:
        raise BundleError(
            _CODE_MISSING, f"the manifest does not declare implementation {implementation!r}"
        )
    entry = _validate_entry(implementation, implementations[implementation])
    payload_name = entry["payload"]
    loader_name = entry["loader"]
    payload = _read_artifact(data_dir, payload_name)
    loader = _read_artifact(data_dir, loader_name)
    if len(payload) != entry["size"]:
        raise BundleError(
            _CODE_INVALID,
            f"{payload_name} is {len(payload)} bytes; the manifest declares {entry['size']}",
        )
    if hashlib.sha256(payload).hexdigest() != entry["sha256"]:
        raise BundleError(_CODE_INVALID, f"{payload_name} does not match its manifest SHA-256")
    if hashlib.sha256(loader).hexdigest() != entry["loader_sha256"]:
        raise BundleError(_CODE_INVALID, f"{loader_name} does not match its manifest SHA-256")
    if len(loader) > MAX_LOADER_BYTES:
        raise BundleError(_CODE_INVALID, f"{loader_name} exceeds the loader size bound")
    try:
        loader_source = loader.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise BundleError(_CODE_INVALID, f"{loader_name} is not valid UTF-8: {exc}") from exc
    hid_stage0_source: str | None = None
    hid_transport_source: str | None = None
    if implementation == "windows-dotnet":
        bootstrap = _validate_hid_bootstrap(implementation, entry)
        hid_stage0_source = _read_hid_artifact(
            data_dir, bootstrap["stage0"], bootstrap["stage0_sha256"],
            "the HID stage0 loader",
        )
        hid_transport_source = _read_hid_artifact(
            data_dir, bootstrap["transport"], bootstrap["transport_sha256"],
            "the HID transport unit",
        )
    elif "hid_bootstrap" in entry:
        # Schema 2 defines exactly one HID bootstrap: the Windows one. A
        # Python entry carrying the block is a malformed manifest, and
        # ignoring it would hide a packaging error.
        raise BundleError(
            _CODE_INVALID,
            f"the manifest entry for {implementation!r} carries hid_bootstrap",
        )
    return SlaveBundle(
        implementation=implementation,
        protocol=PROTOCOL_VERSION,
        filename=payload_name,
        payload=payload,
        loader_source=loader_source,
        sha256=entry["sha256"],
        hid_stage0_source=hid_stage0_source,
        hid_transport_source=hid_transport_source,
    )


def load_bundle(implementation: str) -> SlaveBundle:
    """Returns the validated bundle of one implementation.

    Raises ``BundleError`` with ``slave_bundle_missing`` (artifact absent),
    ``slave_bundle_invalid`` (corrupted manifest, hash or size mismatch) or
    ``slave_bundle_incompatible`` (schema, protocol or version drift). It
    never downloads, rebuilds or falls back to a development copy.
    """
    return _load_from(_data_resource(), implementation)
