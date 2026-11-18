"""Deterministic generation of the Titella slave assets.

The wheel ``titella-slave-assets`` carries immutable, byte-reproducible
artifacts: the stdlib-only Python zipapp, the concatenated Windows C# source,
both loader templates, a manifest that pins size and SHA-256 of every artifact,
and the canonical slave documents byte for byte under ``_data/docs/``. Nothing
is generated into ``src/``; the setuptools ``build_py`` hook writes only into
``build_lib/titella_slave_assets/_data/`` and no sibling checkout is ever
consulted.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import zipfile
from pathlib import Path

try:
    from setuptools.command.build_py import build_py as _SetuptoolsBuildPy
except ImportError:  # pragma: no cover - only hit without setuptools installed
    _SetuptoolsBuildPy = object  # type: ignore[assignment,misc]

_FIXED_TIME = (2020, 1, 1, 0, 0, 0)
_SHEBANG = b"#!/usr/bin/env python3\n"

SCHEMA_VERSION = 4
# Must match [project].version in pyproject.toml and
# titella_slave_assets.__version__; the loader rejects any other value.
ASSETS_VERSION = "1.3.0"

PYTHON_PAYLOAD_NAME = "titella-slave.pyz"
WINDOWS_PAYLOAD_NAME = "titella-slave.cs"
# The Windows agent that needs Python on the target. Same zipapp builder as
# the POSIX payload, different platform modules (HID transport, ConPTY shell,
# GDI capture) collected from their own directory.
WINDOWS_PYTHON_PAYLOAD_NAME = "titella-slave-windows.pyz"
PYTHON_LOADER_NAME = "python.py.tmpl"
WINDOWS_LOADER_NAME = "windows.ps1.tmpl"
WINDOWS_PYTHON_STAGE0_NAME = "windows-python-hid-stage0.ps1.tmpl"
WINDOWS_PYTHON_LOADER_NAME = "windows-python-hid.py.tmpl"
WINDOWS_HID_STAGE0_NAME = "windows-hid-stage0.ps1.tmpl"
WINDOWS_HID_TRANSPORT_NAME = "windows-hid-transport.cs"
WINDOWS_HID_TRANSPORT_SOURCE = "HidByteStream.cs"
# One transport unit per runtime: the C# source above and this stdlib Python
# module. Both implement the same 0x10 flow and are packaged verbatim, so the
# master never receives a second copy that can drift from the compiled agent.
WINDOWS_PYTHON_HID_TRANSPORT_NAME = "windows-python-hid-transport.py"
WINDOWS_PYTHON_HID_TRANSPORT_SOURCE = "hid_transport.py"
MANIFEST_NAME = "manifest.json"

# Upper bound shared with the master's bundle reader: the HID bootstrap
# artifacts are refused above this size instead of being truncated.
MAX_HID_ARTIFACT_BYTES = 64 * 1024

# Canonical slave documents, packaged inside the wheel under ``_data/docs/``
# so the master's documentation tools can serve them without a checkout.
DOCS_DIR_NAME = "docs"
DOCS_SOURCE_DIR = "docs"
DOCUMENTATION_NAMES = (
    "bootstrap-python.md",
    "bootstrap-windows.md",
    "bootstrap-windows-python.md",
    "serial-protocol.md",
)
# Upper bound shared with the master's documentation reader: a document above
# this size is refused instead of truncated.
MAX_DOCUMENT_BYTES = 32 * 1024


class TitellaBuildError(RuntimeError):
    """A required slave asset input is missing or malformed.

    The build fails honestly instead of producing a partial or fake artifact.
    """

    def __init__(self, code: str, message: str) -> None:
        RuntimeError.__init__(self, message)
        self.code = code
        self.message = message


def _protocol_version(root: Path) -> int:
    """Protocol of the canonical wire codec this build packages.

    The manifest must never restate a protocol number by hand: it is read
    from the one codec that every shipped implementation speaks, so the
    built artifacts and the installed loader cannot disagree.
    """
    root = Path(root)
    source = root / "src" / "titella_slave_assets" / "wire.py"
    if not source.is_file():
        raise TitellaBuildError(
            "python_payload_missing", "src/titella_slave_assets/wire.py is missing"
        )
    namespace: dict[str, object] = {}
    exec(compile(source.read_bytes(), str(source), "exec"), namespace)  # noqa: S102
    try:
        return int(namespace["PROTOCOL_VERSION"])
    except (KeyError, TypeError, ValueError):
        raise TitellaBuildError(
            "python_payload_missing", "wire.py does not define PROTOCOL_VERSION"
        ) from None


def _declared_version(source: Path) -> str:
    """``__version__`` of a package ``__init__.py``, without importing it."""
    namespace: dict[str, object] = {}
    exec(compile(Path(source).read_bytes(), str(source), "exec"), namespace)  # noqa: S102
    value = namespace.get("__version__")
    if not isinstance(value, str) or not value:
        raise TitellaBuildError(
            "assets_version_missing", f"{source} does not declare __version__"
        )
    return value


# Modules that only a POSIX host can import: `pty`, `fcntl`, `termios` and
# `pwd` do not exist on Windows, so a Windows payload must not carry them and
# the platform selection in `__main__`/`file_service` never reaches them.
POSIX_ONLY_MODULES = frozenset(("shell_service.py", "screen_service.py"))
# Windows-only modules: the POSIX payload must not carry a ctypes device layer
# or a ConPTY manager that nothing on that platform can import.
WINDOWS_ONLY_MODULES = frozenset((
    "hid_transport.py", "shell_service_win.py", "screen_service_win.py"))


def python_sources(root: Path, runtime: str = "posix") -> list[tuple[str, bytes]]:
    """Flat modules of the zipapp: canonical wire.py plus the agent modules.

    ``runtime`` selects the platform set: ``posix`` carries the POSIX shell and
    screen services, ``windows`` carries the Windows ones and the HID
    transport instead. Everything else is shared byte for byte.
    """
    if runtime not in ("posix", "windows"):
        raise TitellaBuildError("python_payload_missing", f"unknown runtime {runtime!r}")
    root = Path(root)
    wire = root / "src" / "titella_slave_assets" / "wire.py"
    if not wire.is_file():
        raise TitellaBuildError(
            "python_payload_missing", "src/titella_slave_assets/wire.py is missing"
        )
    agent = root / "implementations" / "python"
    files: list[tuple[str, bytes]] = [("wire.py", wire.read_bytes())]
    for path in sorted(agent.glob("*.py"), key=lambda item: item.name):
        if path.name == "__init__.py":
            continue
        if runtime == "posix" and path.name in WINDOWS_ONLY_MODULES:
            # The POSIX agent has no HID transport and no Windows services;
            # carrying them would ship code nothing on that platform can reach.
            continue
        if runtime == "windows" and path.name in POSIX_ONLY_MODULES:
            continue
        files.append((path.name, path.read_bytes()))
    if not any(name == "__main__.py" for name, _ in files):
        raise TitellaBuildError(
            "python_payload_missing", "implementations/python/__main__.py is missing"
        )
    if runtime == "windows":
        for required in ("hid_transport.py", "shell_service_win.py",
                         "screen_service_win.py"):
            if not any(name == required for name, _ in files):
                raise TitellaBuildError(
                    "python_payload_missing",
                    f"implementations/python/{required} is missing",
                )
    return files


def build_zipapp_bytes(root: Path, runtime: str = "posix") -> bytes:
    """Deterministic zipapp: fixed timestamps, entry order, permissions."""
    buffer = io.BytesIO()
    buffer.write(_SHEBANG)
    with zipfile.ZipFile(
        buffer, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
    ) as archive:
        for name, data in python_sources(root, runtime):
            info = zipfile.ZipInfo(name, _FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, data)
    return buffer.getvalue()


def windows_sources(root: Path) -> list[Path]:
    """The C# units in their fixed lexicographic concatenation order."""
    directory = Path(root) / "implementations" / "windows"
    if not directory.is_dir():
        raise TitellaBuildError(
            "windows_sources_missing", "implementations/windows/ does not exist"
        )
    sources = sorted(directory.glob("*.cs"), key=lambda item: item.name)
    if not sources:
        raise TitellaBuildError(
            "windows_sources_missing", "implementations/windows/ contains no .cs units"
        )
    return sources


def build_windows_source(root: Path) -> bytes:
    """Single UTF-8 (no BOM, LF) C# source from the sorted unit list.

    Each unit must enclose its ``using`` directives inside its own namespace
    so that the concatenation stays valid C#.
    """
    units: list[str] = []
    for path in windows_sources(root):
        data = path.read_bytes()
        if data.startswith(b"\xef\xbb\xbf"):
            raise TitellaBuildError(
                "windows_source_bom", f"{path.name} must be UTF-8 without BOM"
            )
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise TitellaBuildError(
                "windows_source_encoding", f"{path.name} is not valid UTF-8: {exc}"
            ) from exc
        units.append(text.replace("\r\n", "\n").replace("\r", "\n").rstrip("\n"))
    return ("\n\n".join(units) + "\n").encode("utf-8")


def loader_bytes(root: Path, filename: str) -> bytes:
    """Raw bytes of a loader template; templates are stored verbatim."""
    path = Path(root) / "loaders" / filename
    if not path.is_file():
        raise TitellaBuildError("loader_missing", f"loaders/{filename} is missing")
    return path.read_bytes()


def windows_python_transport_source(root: Path) -> bytes:
    """The stdlib Python HID transport unit, packaged verbatim.

    Sibling of ``hid_transport_source``: one implementation of the 0x10 report
    protocol per runtime. The packaged ``windows-python-hid-transport.py`` is a
    byte copy of ``implementations/python/hid_transport.py``, so the master
    never stages a second copy that can drift from the payload it starts.
    """
    path = Path(root) / "implementations" / "python" / WINDOWS_PYTHON_HID_TRANSPORT_SOURCE
    if not path.is_file():
        raise TitellaBuildError(
            "hid_transport_missing",
            f"implementations/python/{WINDOWS_PYTHON_HID_TRANSPORT_SOURCE} is missing",
        )
    data = path.read_bytes()
    if data.startswith(b"\xef\xbb\xbf"):
        raise TitellaBuildError(
            "hid_transport_invalid",
            f"{WINDOWS_PYTHON_HID_TRANSPORT_SOURCE} must be UTF-8 without BOM",
        )
    try:
        data.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise TitellaBuildError(
            "hid_transport_invalid",
            f"{WINDOWS_PYTHON_HID_TRANSPORT_SOURCE} is not valid UTF-8: {exc}",
        ) from exc
    return data


def hid_transport_source(root: Path) -> bytes:
    """The sole .NET HID transport unit, packaged verbatim.

    There is exactly one implementation of the 0x10 report protocol,
    ``implementations/windows/HidByteStream.cs``. The packaged
    ``windows-hid-transport.cs`` is a byte copy of it, so the master never
    receives a second copy that can drift from the compiled agent.
    """
    path = Path(root) / "implementations" / "windows" / WINDOWS_HID_TRANSPORT_SOURCE
    if not path.is_file():
        raise TitellaBuildError(
            "hid_transport_missing",
            f"implementations/windows/{WINDOWS_HID_TRANSPORT_SOURCE} is missing",
        )
    data = path.read_bytes()
    if data.startswith(b"\xef\xbb\xbf"):
        raise TitellaBuildError(
            "hid_transport_invalid",
            f"{WINDOWS_HID_TRANSPORT_SOURCE} must be UTF-8 without BOM",
        )
    try:
        data.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise TitellaBuildError(
            "hid_transport_invalid",
            f"{WINDOWS_HID_TRANSPORT_SOURCE} is not valid UTF-8: {exc}",
        ) from exc
    return data


def hid_bootstrap_entry(stage0: bytes, transport: bytes,
                        stage0_name: str = WINDOWS_HID_STAGE0_NAME,
                        transport_name: str = WINDOWS_HID_TRANSPORT_NAME
                        ) -> dict[str, object]:
    """One implementation's ``hid_bootstrap`` block: keys and byte hashes."""
    return {
        "stage0": stage0_name,
        "stage0_sha256": hashlib.sha256(stage0).hexdigest(),
        "transport": transport_name,
        "transport_sha256": hashlib.sha256(transport).hexdigest(),
    }


def _require_hid_artifact(name: str, data: bytes) -> None:
    """Both HID artifacts are bounded before they ever reach the manifest."""
    if not data:
        raise TitellaBuildError("hid_artifact_invalid", f"{name} is empty")
    if len(data) > MAX_HID_ARTIFACT_BYTES:
        raise TitellaBuildError(
            "hid_artifact_too_large",
            f"{name} is {len(data)} bytes; the HID artifact bound is "
            f"{MAX_HID_ARTIFACT_BYTES}",
        )


def manifest_entry(payload_name: str, loader_name: str, payload: bytes,
                   loader: bytes) -> dict[str, object]:
    """Manifest entry with size and hashes computed from the built bytes."""
    return {
        "payload": payload_name,
        "loader": loader_name,
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "loader_sha256": hashlib.sha256(loader).hexdigest(),
    }


def bundle_artifacts(root: Path) -> tuple[dict[str, object], list[tuple[str, bytes]]]:
    """Manifest plus every artifact it describes, except the manifest itself.

    Every input is required: a missing implementation source or loader fails
    the build instead of emitting a partial bundle.
    """
    root = Path(root)
    # The manifest version must match the installed package version the
    # loader validates against; a mismatched bump must fail the build, not
    # ship an incompatible wheel.
    version_source = root / "src" / "titella_slave_assets" / "__init__.py"
    if not version_source.is_file():
        raise TitellaBuildError(
            "python_payload_missing", "src/titella_slave_assets/__init__.py is missing"
        )
    package_version = _declared_version(version_source)
    if package_version != ASSETS_VERSION:
        raise TitellaBuildError(
            "assets_version_mismatch",
            "titella_slave_assets.__version__ is %r but the build declares %r"
            % (package_version, ASSETS_VERSION),
        )
    python_payload = build_zipapp_bytes(root, "posix")
    python_loader = loader_bytes(root, PYTHON_LOADER_NAME)
    windows_payload = build_windows_source(root)
    windows_loader = loader_bytes(root, WINDOWS_LOADER_NAME)
    hid_stage0 = loader_bytes(root, WINDOWS_HID_STAGE0_NAME)
    _require_hid_artifact(WINDOWS_HID_STAGE0_NAME, hid_stage0)
    hid_transport = hid_transport_source(root)
    _require_hid_artifact(WINDOWS_HID_TRANSPORT_NAME, hid_transport)
    windows_entry = manifest_entry(
        WINDOWS_PAYLOAD_NAME, WINDOWS_LOADER_NAME, windows_payload, windows_loader
    )
    windows_entry["hid_bootstrap"] = hid_bootstrap_entry(hid_stage0, hid_transport)

    windows_python_payload = build_zipapp_bytes(root, "windows")
    windows_python_loader = loader_bytes(root, WINDOWS_PYTHON_LOADER_NAME)
    windows_python_stage0 = loader_bytes(root, WINDOWS_PYTHON_STAGE0_NAME)
    _require_hid_artifact(WINDOWS_PYTHON_STAGE0_NAME, windows_python_stage0)
    windows_python_transport = windows_python_transport_source(root)
    _require_hid_artifact(WINDOWS_PYTHON_HID_TRANSPORT_NAME, windows_python_transport)
    windows_python_entry = manifest_entry(
        WINDOWS_PYTHON_PAYLOAD_NAME, WINDOWS_PYTHON_LOADER_NAME,
        windows_python_payload, windows_python_loader,
    )
    windows_python_entry["hid_bootstrap"] = hid_bootstrap_entry(
        windows_python_stage0, windows_python_transport,
        stage0_name=WINDOWS_PYTHON_STAGE0_NAME,
        transport_name=WINDOWS_PYTHON_HID_TRANSPORT_NAME,
    )

    # The DLL variant is the same C# source, the same loader template and the
    # same HID artifacts as `windows-dotnet`: only the loader's compilation
    # mode differs (the master renders `__AGENT_FORMAT__`). The entry names
    # the existing artifacts instead of shipping a second copy, so the bytes
    # cannot drift between the two variants.
    windows_dll_entry = manifest_entry(
        WINDOWS_PAYLOAD_NAME, WINDOWS_LOADER_NAME, windows_payload, windows_loader
    )
    windows_dll_entry["hid_bootstrap"] = hid_bootstrap_entry(hid_stage0, hid_transport)

    manifest: dict[str, object] = {
        "schema_version": SCHEMA_VERSION,
        "protocol": _protocol_version(root),
        "version": ASSETS_VERSION,
        "implementations": {
            "python": manifest_entry(
                PYTHON_PAYLOAD_NAME, PYTHON_LOADER_NAME, python_payload, python_loader
            ),
            "windows-dotnet": windows_entry,
            "windows-dotnet-dll": windows_dll_entry,
            "windows-python": windows_python_entry,
        },
    }
    artifacts: list[tuple[str, bytes]] = [
        (PYTHON_PAYLOAD_NAME, python_payload),
        (PYTHON_LOADER_NAME, python_loader),
        (WINDOWS_PAYLOAD_NAME, windows_payload),
        (WINDOWS_LOADER_NAME, windows_loader),
        (WINDOWS_HID_STAGE0_NAME, hid_stage0),
        (WINDOWS_HID_TRANSPORT_NAME, hid_transport),
        (WINDOWS_PYTHON_PAYLOAD_NAME, windows_python_payload),
        (WINDOWS_PYTHON_LOADER_NAME, windows_python_loader),
        (WINDOWS_PYTHON_STAGE0_NAME, windows_python_stage0),
        (WINDOWS_PYTHON_HID_TRANSPORT_NAME, windows_python_transport),
    ]
    return manifest, artifacts


def render_manifest(manifest: dict[str, object]) -> str:
    """Deterministic manifest text: sorted keys, compact separators."""
    return json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n"


def documentation_sources(root: Path) -> list[tuple[str, bytes]]:
    """The canonical slave documents in their fixed packaging order.

    The three files are required and validated here, so a wheel can never ship
    an empty, oversized or non-UTF-8 document that the master would then have
    to refuse. Copies are byte-identical: documentation is data, not generated
    content.
    """
    root = Path(root)
    files: list[tuple[str, bytes]] = []
    for name in DOCUMENTATION_NAMES:
        path = root / DOCS_SOURCE_DIR / name
        if not path.is_file():
            raise TitellaBuildError(
                "docs_missing", f"{DOCS_SOURCE_DIR}/{name} is missing"
            )
        payload = path.read_bytes()
        if not payload:
            raise TitellaBuildError(
                "docs_invalid", f"{DOCS_SOURCE_DIR}/{name} is empty"
            )
        if len(payload) > MAX_DOCUMENT_BYTES:
            raise TitellaBuildError(
                "docs_invalid",
                f"{DOCS_SOURCE_DIR}/{name} is {len(payload)} bytes, above the "
                f"{MAX_DOCUMENT_BYTES}-byte limit",
            )
        try:
            payload.decode("utf-8")
        except UnicodeDecodeError:
            raise TitellaBuildError(
                "docs_invalid", f"{DOCS_SOURCE_DIR}/{name} is not valid UTF-8"
            ) from None
        files.append((f"{DOCS_DIR_NAME}/{name}", payload))
    return files


def write_bundle_assets(root: Path, data_dir: Path) -> None:
    """Materializes the bundle artifacts into ``data_dir``.

    Every input is validated before the first byte is written, so a failed
    build never leaves a partial ``_data`` directory. Payloads and loaders are
    written before the manifest so that a manifest never references absent
    files. The canonical documents are copied afterwards into ``docs/``; they
    are not manifest payloads and never alter the bundle hashes.
    """
    manifest, artifacts = bundle_artifacts(root)
    documents = documentation_sources(root)
    data_dir = Path(data_dir)
    data_dir.mkdir(parents=True, exist_ok=True)
    for name, data in artifacts:
        (data_dir / name).write_bytes(data)
    (data_dir / MANIFEST_NAME).write_bytes(render_manifest(manifest).encode("utf-8"))
    (data_dir / DOCS_DIR_NAME).mkdir(parents=True, exist_ok=True)
    for name, data in documents:
        (data_dir / name).write_bytes(data)


def build(output: str | os.PathLike[str], root: str | os.PathLike[str] | None = None
          ) -> dict[str, object]:
    """Writes the deterministic Python zipapp to ``output``.

    Kept as a small development entry point for the test suite; the wheel is
    produced by the setuptools hook, not by this function.
    """
    source_root = Path(root) if root is not None else Path(__file__).resolve().parent
    destination = Path(output).expanduser().resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = build_zipapp_bytes(source_root)
    temporary = destination.with_name(f".{destination.name}.tmp")
    temporary.write_bytes(payload)
    temporary.chmod(0o700)
    os.replace(temporary, destination)
    return {
        "path": str(destination),
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
    }


class SlaveAssetsBuildPy(_SetuptoolsBuildPy):  # type: ignore[misc,valid-type]
    """setuptools ``build_py`` hook that generates the bundle data.

    Runs after the regular package copy and writes only inside
    ``build_lib/titella_slave_assets/_data/``; the source tree and any
    sibling repository are never touched.
    """

    def run(self) -> None:
        _SetuptoolsBuildPy.run(self)
        root = Path(__file__).resolve().parent
        data_dir = Path(self.build_lib) / "titella_slave_assets" / "_data"
        write_bundle_assets(root, data_dir)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description="Builds titella-slave.pyz deterministically")
    parser.add_argument("-o", "--output", default="dist/titella-slave.pyz")
    parser.add_argument(
        "--root",
        help="repository or unpacked-sdist root; defaults to this file's directory",
    )
    args = parser.parse_args(argv)
    result = build(args.output, root=args.root)
    print(f"{result['path']} {result['size']} bytes sha256={result['sha256']}")


if __name__ == "__main__":
    main()
