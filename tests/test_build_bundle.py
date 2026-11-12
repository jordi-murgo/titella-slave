"""Behavioral tests of the deterministic slave asset build and bundle API.

Covers: byte-identical rebuilds across source mtime churn, the manifest
contract (sizes and hashes computed from built bytes), path-traversal
rejection, and every declared BundleError code — without any HID, master
or wheel-building dependency.

The bundle validation core (``_load_from``) takes the data directory as a
``Traversable`` so these tests exercise the exact production validation
against synthetic data directories. Loading from the installed wheel is
exercised end to end by the master integration suite, which installs the
wheel and injects no ``PYTHONPATH``.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import shutil
import tempfile
import unittest
import zipfile
from pathlib import Path

import build_support
from titella_slave_assets import __version__
from titella_slave_assets import bundles

ROOT = Path(__file__).resolve().parents[1]

WINDOWS_DIR = ROOT / "implementations" / "windows"


def _entry(payload_name: str, loader_name: str, payload: bytes,
           loader: bytes) -> dict[str, object]:
    """A manifest entry whose size and hashes derive from the real bytes."""
    return {
        "payload": payload_name,
        "loader": loader_name,
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "loader_sha256": hashlib.sha256(loader).hexdigest(),
    }


HID_STAGE0 = b"# hid stage0\n__HID_TOKEN__ __HID_SIZE__ __HID_SHA256__\n"
HID_TRANSPORT = b"// hid transport unit\n"


def _hid_bootstrap(stage0: bytes = HID_STAGE0,
                   transport: bytes = HID_TRANSPORT) -> dict[str, object]:
    """The Windows ``hid_bootstrap`` block, hashes derived from the real bytes."""
    return {
        "stage0": build_support.WINDOWS_HID_STAGE0_NAME,
        "stage0_sha256": hashlib.sha256(stage0).hexdigest(),
        "transport": build_support.WINDOWS_HID_TRANSPORT_NAME,
        "transport_sha256": hashlib.sha256(transport).hexdigest(),
    }


def _manifest_for(python_payload: bytes, windows_payload: bytes,
                  loader: bytes = b"# loader\n",
                  windows_loader: bytes = b"# windows loader\n",
                  hid_stage0: bytes = HID_STAGE0,
                  hid_transport: bytes = HID_TRANSPORT) -> dict[str, object]:
    windows = _entry("titella-slave.cs", "windows.ps1.tmpl",
                     windows_payload, windows_loader)
    windows["hid_bootstrap"] = _hid_bootstrap(hid_stage0, hid_transport)
    return {
        "schema_version": 2,
        "protocol": 1,
        "version": __version__,
        "implementations": {
            "python": _entry("titella-slave.pyz", "python.py.tmpl",
                             python_payload, loader),
            "windows-dotnet": windows,
        },
    }


def _data_dir_with(case: unittest.TestCase, manifest: dict[str, object],
                   payload: bytes, loader: bytes = b"# loader\n",
                   windows_payload: bytes = b"// cs",
                   hid_stage0: bytes = HID_STAGE0,
                   hid_transport: bytes = HID_TRANSPORT) -> Path:
    """A synthetic installed ``_data`` directory described by ``manifest``.

    Artifacts are written under their canonical packaged names; a test that
    needs a malformed name rewrites ``manifest.json`` afterwards, so no path
    ever escapes this temporary directory. The directory is removed with the
    test case; no state survives a run.
    """
    directory = Path(tempfile.mkdtemp(prefix="titella-bundle-"))
    case.addCleanup(shutil.rmtree, directory, ignore_errors=True)
    entry = manifest["implementations"]["python"]
    (directory / entry["payload"]).write_bytes(payload)
    (directory / entry["loader"]).write_bytes(loader)
    if "windows-dotnet" in manifest["implementations"]:
        windows = manifest["implementations"]["windows-dotnet"]
        (directory / windows["payload"]).write_bytes(windows_payload)
        (directory / windows["loader"]).write_bytes(b"# windows loader\n")
        (directory / build_support.WINDOWS_HID_STAGE0_NAME).write_bytes(hid_stage0)
        (directory / build_support.WINDOWS_HID_TRANSPORT_NAME).write_bytes(hid_transport)
    (directory / "manifest.json").write_text(
        json.dumps(manifest), encoding="utf-8"
    )
    return directory


class BuildDeterminismTests(unittest.TestCase):
    def test_zipapp_is_byte_identical_across_rebuilds_and_mtime_churn(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            first = Path(temp) / "first.pyz"
            second = Path(temp) / "second.pyz"
            first_meta = build_support.build(first, root=ROOT)
            # Churn the mtimes of every input: a fixed-time archive must not
            # react to the filesystem clock. The original values are restored
            # afterwards so the checkout is not mutated.
            touched = sorted(ROOT.glob("implementations/python/*.py"))
            original = [(path.stat().st_atime, path.stat().st_mtime) for path in touched]
            try:
                for path in touched:
                    os.utime(path, (1_600_000_000, 1_600_000_000))
                second_meta = build_support.build(second, root=ROOT)
            finally:
                for path, (atime, mtime) in zip(touched, original):
                    os.utime(path, (atime, mtime))
            self.assertEqual(first_meta["sha256"], second_meta["sha256"])
            self.assertEqual(first.read_bytes(), second.read_bytes())

    def test_zipapp_keeps_flat_modules_and_fixed_metadata(self) -> None:
        payload = build_support.build_zipapp_bytes(ROOT)
        self.assertTrue(payload.startswith(b"#!/usr/bin/env python3\n"))
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            infos = archive.infolist()
        names = [info.filename for info in infos]
        self.assertIn("__main__.py", names)
        self.assertIn("wire.py", names)
        self.assertNotIn("titella_slave_assets/wire.py", names)
        # wire.py inside the zipapp is the canonical codec, byte for byte.
        canonical = (ROOT / "src" / "titella_slave_assets" / "wire.py").read_bytes()
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            self.assertEqual(archive.read("wire.py"), canonical)
        for info in infos:
            self.assertEqual(info.date_time, (2020, 1, 1, 0, 0, 0))
            self.assertEqual(info.external_attr, 0o644 << 16)
            self.assertEqual(info.compress_type, zipfile.ZIP_DEFLATED)

    def test_missing_wire_or_main_is_reported_honestly(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            # A root without implementations/ must fail naming the input, not
            # produce an empty zipapp.
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.build_zipapp_bytes(Path(temp))

    def test_build_resolves_inputs_from_the_sdist_root_not_cwd(self) -> None:
        """The sdist rebuild runs from an unpacked directory: the build must
        find its inputs relative to the repository/sdist root it is given,
        not relative to the invocation working directory."""
        import subprocess
        import sys

        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "away.pyz"
            script = ROOT / "build_support.py"
            # Invoke from an unrelated cwd with an explicit root; the
            # zipapp bytes must be identical to an in-tree build.
            completed = subprocess.run(
                [sys.executable, str(script), "-o", str(output), "--root", str(ROOT)],
                cwd=temp, capture_output=True, text=True, check=True,
            )
            self.assertEqual(
                hashlib.sha256(output.read_bytes()).hexdigest(),
                hashlib.sha256(build_support.build_zipapp_bytes(ROOT)).hexdigest(),
            )

    def test_missing_loader_is_reported_honestly(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            # A root without loaders/ must fail naming the input, never
            # produce a manifest that references an absent loader.
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.loader_bytes(Path(temp), build_support.PYTHON_LOADER_NAME)
            self.assertEqual(raised.exception.code, "loader_missing")

    def test_windows_source_is_single_utf8_no_bom_lf_concatenation(self) -> None:
        data = build_support.build_windows_source(ROOT)
        self.assertFalse(data.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r\n", data)
        self.assertTrue(data.endswith(b"\n"))
        order = [path.name for path in build_support.windows_sources(ROOT)]
        self.assertEqual(order, sorted(order))
        # The csc /main of the loader names Titella.Slave.AgentHost, so the
        # entry unit must exist and stay in the concatenation order.
        self.assertIn("AgentHost.cs", order)
        self.assertLess(order.index("Agent.cs"), order.index("AgentHost.cs"))
        self.assertLess(order.index("AgentHost.cs"), order.index("CdcTransport.cs"))
        self.assertIn(b"public static int Main(string[] args)", data)

    def test_missing_windows_sources_are_reported_honestly(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.build_windows_source(Path(temp))
            self.assertEqual(raised.exception.code, "windows_sources_missing")
            # A synthetic root with every other input present: the failure must
            # name the missing Windows unit, never emit a partial bundle.
            root = Path(temp)
            assets = root / "src" / "titella_slave_assets"
            assets.mkdir(parents=True)
            for name in ("wire.py", "__init__.py"):
                (assets / name).write_bytes(
                    (ROOT / "src" / "titella_slave_assets" / name).read_bytes()
                )
            (root / "implementations" / "python").mkdir(parents=True)
            for name in ("__main__.py", "file_service.py", "shell_service.py",
                         "transfer_service.py", "agent_link.py"):
                (root / "implementations" / "python" / name).write_bytes(b"# unit\n")
            (root / "loaders").mkdir()
            (root / "loaders" / build_support.PYTHON_LOADER_NAME).write_bytes(b"x")
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.bundle_artifacts(root)
            self.assertEqual(raised.exception.code, "windows_sources_missing")

    def test_windows_loader_template_is_present_and_policy_neutral(self) -> None:
        """The loader ships as readable source: no execution-policy bypass,
        no unblocking, no hidden window and no script file invocation."""
        source = build_support.loader_bytes(ROOT, build_support.WINDOWS_LOADER_NAME)
        self.assertFalse(source.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r\n", source)
        text = source.decode("utf-8")
        for placeholder in ("__LINK_ID__", "__SESSION_ID__", "__CDC_PORT__"):
            self.assertIn(placeholder, text)
        for forbidden in ("ExecutionPolicy Bypass", "Unblock-File", "-WindowStyle Hidden"):
            self.assertNotIn(forbidden, text)
        # The payload is compiled to agent.exe with the framework's own csc
        # and started directly: no powershell.exe child, no Add-Type payload
        # compile, no -EncodedCommand, never invoked as a .ps1 file with -File.
        self.assertIn("csc.exe", text)
        self.assertIn("/target:exe", text)
        self.assertIn("/main:Titella.Slave.AgentHost", text)
        self.assertIn("agent.json", text)
        self.assertNotIn("-EncodedCommand", text)
        self.assertNotIn("-File ", text)
        self.assertIn("FreeConsole", build_support.build_windows_source(ROOT).decode("utf-8"))

    def test_manifest_hashes_and_sizes_come_from_built_bytes(self) -> None:
        """Every manifest value must derive from the built bytes."""
        with tempfile.TemporaryDirectory() as temp:
            data_dir = Path(temp) / "_data"
            build_support.write_bundle_assets(ROOT, data_dir)
            manifest = json.loads(
                (data_dir / "manifest.json").read_text(encoding="utf-8")
            )
            self.assertEqual(manifest["schema_version"], 2)
            self.assertEqual(manifest["protocol"], 1)
            self.assertEqual(manifest["version"], __version__)
            self.assertEqual(
                set(manifest["implementations"]), {"python", "windows-dotnet"}
            )
            for entry in manifest["implementations"].values():
                payload = (data_dir / entry["payload"]).read_bytes()
                loader = (data_dir / entry["loader"]).read_bytes()
                self.assertEqual(entry["size"], len(payload))
                self.assertEqual(
                    entry["sha256"], hashlib.sha256(payload).hexdigest()
                )
                self.assertEqual(
                    entry["loader_sha256"], hashlib.sha256(loader).hexdigest()
                )
            windows = manifest["implementations"]["windows-dotnet"]
            self.assertEqual(
                set(windows["hid_bootstrap"]),
                set(bundles.HID_BOOTSTRAP_KEYS),
            )
            bootstrap = windows["hid_bootstrap"]
            stage0 = (data_dir / bootstrap["stage0"]).read_bytes()
            transport = (data_dir / bootstrap["transport"]).read_bytes()
            self.assertEqual(
                bootstrap["stage0_sha256"], hashlib.sha256(stage0).hexdigest()
            )
            self.assertEqual(
                bootstrap["transport_sha256"], hashlib.sha256(transport).hexdigest()
            )
            self.assertNotIn("hid_bootstrap", manifest["implementations"]["python"])
            first = (data_dir / "manifest.json").read_bytes()
            build_support.write_bundle_assets(ROOT, data_dir)
            self.assertEqual(first, (data_dir / "manifest.json").read_bytes())

    def test_missing_or_oversized_hid_inputs_fail_the_build(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.loader_bytes(root, build_support.WINDOWS_HID_STAGE0_NAME)
            self.assertEqual(raised.exception.code, "loader_missing")
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.hid_transport_source(root)
            self.assertEqual(raised.exception.code, "hid_transport_missing")
            # An oversized artifact is refused at build time, not shipped and
            # truncated later by the bundle reader.
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support._require_hid_artifact(
                    "windows-hid-transport.cs",
                    b"x" * (build_support.MAX_HID_ARTIFACT_BYTES + 1),
                )
            self.assertEqual(raised.exception.code, "hid_artifact_too_large")


    def test_python_loader_has_no_product_fallback_and_no_env_port_channel(self) -> None:
        """The extracted loader accepts only the override the master renders.

        A stale product string is not a second identity, and the CDC path is
        not read from the environment: that would be an undocumented channel.
        """
        source = build_support.loader_bytes(ROOT, build_support.PYTHON_LOADER_NAME).decode("utf-8")
        self.assertNotIn("SbD HID Auditor", source)
        self.assertIn("Titella KVM", source)
        self.assertNotIn("TITELLA_CDC_PORT", source)
        self.assertIn("P=__CDC_PORT__", source)
        self.assertIn("/dev/ttyACM*", source)
        # Bounded framing: the loader drops an oversized line whole instead of
        # buffering without limit, then resynchronises on the next LF.
        self.assertIn("4097", source)
        self.assertIn("4096", source)
        self.assertIn('buf=buf[nl+1:]', source)

    def test_python_loader_template_renders_a_safe_cdc_literal(self) -> None:
        """The override lands as a Python literal, never as raw concatenation."""
        source = build_support.loader_bytes(ROOT, build_support.PYTHON_LOADER_NAME).decode("utf-8")
        port = "/dev/cu.usbmodem'quoted"
        rendered = source.replace("__LINK_ID__", "0" * 32).replace(
            "__SESSION_ID__", "1" * 32
        ).replace("__CDC_PORT__", repr(port))
        self.assertIn("P=" + repr(port), rendered)
        compile(rendered, "<loader>", "exec")


class BundleValidationTests(unittest.TestCase):
    """The exact production validation against synthetic data directories."""

    def test_valid_bundle_is_returned_with_payload_and_loader(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        bundle = bundles._load_from(data_dir, "python")
        self.assertEqual(bundle.implementation, "python")
        self.assertEqual(bundle.protocol, 1)
        self.assertEqual(bundle.filename, "titella-slave.pyz")
        self.assertEqual(bundle.payload, payload)
        self.assertEqual(bundle.sha256, hashlib.sha256(payload).hexdigest())
        self.assertEqual(bundle.loader_source, "# loader\n")
        # Python has no HID transport: schema 2 keeps both fields None.
        self.assertIsNone(bundle.hid_stage0_source)
        self.assertIsNone(bundle.hid_transport_source)
        self.assertIsInstance(bundle, bundles.SlaveBundle)

    def test_bundle_is_immutable(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        bundle = bundles._load_from(data_dir, "python")
        with self.assertRaises(Exception):
            bundle.implementation = "other"  # type: ignore[misc]

    def test_unknown_implementation_is_invalid(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "no-such-implementation")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_implementation_absent_from_manifest_is_missing(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        del manifest["implementations"]["windows-dotnet"]
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_MISSING)

    def test_absent_manifest_reports_missing(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        (data_dir / "manifest.json").unlink()
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_MISSING)

    def test_missing_artifact_reports_missing(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        (data_dir / "titella-slave.pyz").unlink()
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_MISSING)

    def test_corrupt_payload_hash_reports_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["python"]["sha256"] = "0" * 64
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_corrupt_loader_hash_reports_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["python"]["loader_sha256"] = "0" * 64
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_size_mismatch_reports_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["python"]["size"] = len(payload) + 1
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_malformed_manifest_json_reports_invalid(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        (data_dir / "manifest.json").write_text("not-json", encoding="utf-8")
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_empty_manifest_reports_invalid(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        (data_dir / "manifest.json").write_bytes(b"")
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_manifest_size_bound_is_enforced(self) -> None:
        payload = b"payload-bytes"
        data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
        (data_dir / "manifest.json").write_text(
            "x" * (bundles.MAX_MANIFEST_BYTES + 1), encoding="utf-8"
        )
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_entry_omitting_a_key_reports_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        del manifest["implementations"]["python"]["loader_sha256"]
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_loader_size_bound_is_enforced(self) -> None:
        payload = b"payload-bytes"
        huge = b"x" * (bundles.MAX_LOADER_BYTES + 1)
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["python"]["loader_sha256"] = (
            hashlib.sha256(huge).hexdigest()
        )
        data_dir = _data_dir_with(self, manifest, payload, loader=huge)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_protocol_drift_reports_incompatible(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["protocol"] = 2
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INCOMPATIBLE)

    def test_schema_drift_reports_incompatible(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["schema_version"] = 99
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INCOMPATIBLE)

    def test_version_drift_reports_incompatible(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["version"] = "0.9.9"
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INCOMPATIBLE)

    def test_schema_one_manifest_is_rejected(self) -> None:
        """Clean cutover: schema 1 is not accepted in parallel."""
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["schema_version"] = 1
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INCOMPATIBLE)


class HidBootstrapValidationTests(unittest.TestCase):
    """Schema 2: the Windows bundle must carry both validated HID artifacts."""

    def test_missing_hid_bootstrap_block_is_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        del manifest["implementations"]["windows-dotnet"]["hid_bootstrap"]
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_hid_bootstrap_block_must_have_exactly_the_contract_keys(self) -> None:
        payload = b"payload-bytes"
        # Omitting one contract key fails like a schema violation.
        manifest = _manifest_for(payload, b"// cs")
        del manifest["implementations"]["windows-dotnet"]["hid_bootstrap"]["stage0_sha256"]
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)
        # An extra key is equally malformed: the block is fixed, not open.
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["windows-dotnet"]["hid_bootstrap"]["extra"] = "x"
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_malformed_hid_hashes_are_invalid(self) -> None:
        payload = b"payload-bytes"
        for key in ("stage0_sha256", "transport_sha256"):
            manifest = _manifest_for(payload, b"// cs")
            manifest["implementations"]["windows-dotnet"]["hid_bootstrap"][key] = "0" * 63
            data_dir = _data_dir_with(self, manifest, payload)
            with self.assertRaises(bundles.BundleError) as raised:
                bundles._load_from(data_dir, "windows-dotnet")
            self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_tampered_hid_stage0_hash_is_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["windows-dotnet"]["hid_bootstrap"]["stage0_sha256"] = "0" * 64
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_tampered_hid_transport_hash_is_invalid(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["windows-dotnet"]["hid_bootstrap"]["transport_sha256"] = "0" * 64
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_absent_hid_artifacts_report_missing(self) -> None:
        payload = b"payload-bytes"
        for member in (build_support.WINDOWS_HID_STAGE0_NAME,
                       build_support.WINDOWS_HID_TRANSPORT_NAME):
            data_dir = _data_dir_with(self, _manifest_for(payload, b"// cs"), payload)
            (data_dir / member).unlink()
            with self.assertRaises(bundles.BundleError) as raised:
                bundles._load_from(data_dir, "windows-dotnet")
            self.assertEqual(raised.exception.code, bundles.BUNDLE_MISSING)

    def test_unsafe_hid_artifact_names_are_invalid(self) -> None:
        payload = b"payload-bytes"
        for name in ("../escape.cs", "/etc/passwd", "a\\b"):
            manifest = _manifest_for(payload, b"// cs")
            manifest["implementations"]["windows-dotnet"]["hid_bootstrap"]["transport"] = name
            data_dir = _data_dir_with(self, manifest, payload)
            with self.assertRaises(bundles.BundleError) as raised:
                bundles._load_from(data_dir, "windows-dotnet")
            self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_non_utf8_hid_artifact_is_invalid(self) -> None:
        payload = b"payload-bytes"
        invalid = b"\xff\xfe not utf-8"
        manifest = _manifest_for(payload, b"// cs", hid_stage0=invalid)
        data_dir = _data_dir_with(
            self, manifest, payload, hid_stage0=invalid,
        )
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_hid_artifact_size_bound_is_enforced(self) -> None:
        """64 KiB is the boundary: exactly at it loads, one byte over fails."""
        payload = b"payload-bytes"
        at_limit = b"a" * bundles.MAX_HID_ARTIFACT_BYTES
        manifest = _manifest_for(payload, b"// cs", hid_transport=at_limit)
        data_dir = _data_dir_with(
            self, manifest, payload, hid_transport=at_limit,
        )
        bundle = bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(len(bundle.hid_transport_source), bundles.MAX_HID_ARTIFACT_BYTES)
        over_limit = b"a" * (bundles.MAX_HID_ARTIFACT_BYTES + 1)
        manifest = _manifest_for(payload, b"// cs", hid_transport=over_limit)
        data_dir = _data_dir_with(
            self, manifest, payload, hid_transport=over_limit,
        )
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "windows-dotnet")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_python_entry_must_not_carry_hid_bootstrap(self) -> None:
        payload = b"payload-bytes"
        manifest = _manifest_for(payload, b"// cs")
        manifest["implementations"]["python"]["hid_bootstrap"] = _hid_bootstrap()
        data_dir = _data_dir_with(self, manifest, payload)
        with self.assertRaises(bundles.BundleError) as raised:
            bundles._load_from(data_dir, "python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)


class ManifestPathSafetyTests(unittest.TestCase):
    def test_traversal_and_absolute_names_are_rejected(self) -> None:
        for bad in ("", ".", "..", "../wire.py", "/etc/passwd", "a/b", "a\\b",
                    "C:evil", " name", "name ", "~root", "nul\x00name"):
            with self.assertRaises(bundles.BundleError) as raised:
                bundles._safe_member(bad)
            self.assertEqual(raised.exception.code, bundles.BUNDLE_INVALID)

    def test_plain_filename_is_accepted(self) -> None:
        self.assertEqual(bundles._safe_member("titella-slave.pyz"), "titella-slave.pyz")


class DocumentationPackagingTests(unittest.TestCase):
    """The canonical slave documents travel inside the wheel, unmodified.

    The master reads them from ``titella_slave_assets/_data/docs/`` with
    ``importlib.resources``: a missing, empty, oversized or non-UTF-8 document
    must fail the build here instead of shipping an unreadable asset.
    """

    def test_sources_are_copied_byte_identically_in_fixed_order(self) -> None:
        files = build_support.documentation_sources(ROOT)
        self.assertEqual(
            [name for name, _ in files],
            ["docs/" + name for name in build_support.DOCUMENTATION_NAMES],
        )
        # Exactly one editable copy per document: no superseded duplicate
        # (for example an old bootstrap.md) may linger in docs/.
        self.assertEqual(
            sorted(path.name for path in (ROOT / "docs").glob("*.md")),
            sorted(build_support.DOCUMENTATION_NAMES),
        )
        for name, payload in files:
            source = ROOT / name
            self.assertEqual(payload, source.read_bytes())
            self.assertGreater(len(payload), 0)
            self.assertLessEqual(len(payload), build_support.MAX_DOCUMENT_BYTES)
            payload.decode("utf-8")  # strict: an invalid document fails here

    def test_bundled_data_dir_carries_docs_but_the_manifest_does_not(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            data_dir = Path(temp) / "_data"
            build_support.write_bundle_assets(ROOT, data_dir)
            manifest = json.loads(
                (data_dir / "manifest.json").read_text(encoding="utf-8")
            )
            # Documentation is not a bundle payload: the pinned manifest keeps
            # exactly the two implementations.
            self.assertEqual(
                set(manifest["implementations"]), {"python", "windows-dotnet"}
            )
            docs_dir = data_dir / "docs"
            packaged = sorted(path.name for path in docs_dir.iterdir())
            self.assertEqual(packaged, sorted(build_support.DOCUMENTATION_NAMES))
            for name in packaged:
                self.assertEqual(
                    (docs_dir / name).read_bytes(),
                    (ROOT / "docs" / name).read_bytes(),
                )
            # Re-running the hook must not change a single packaged byte.
            before = {name: (docs_dir / name).read_bytes() for name in packaged}
            build_support.write_bundle_assets(ROOT, data_dir)
            for name, payload in before.items():
                self.assertEqual((docs_dir / name).read_bytes(), payload)

    def test_sdist_manifest_ships_the_documents(self) -> None:
        """The hook reads docs/ from the sdist root: it must travel there."""
        manifest = (ROOT / "MANIFEST.in").read_text(encoding="utf-8")
        self.assertTrue(
            any(line.strip().startswith("include docs/") and "*.md" in line
                for line in manifest.splitlines()),
            "MANIFEST.in must include docs/*.md so a wheel can be built from the sdist",
        )

    def test_missing_document_fails_the_build(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "docs").mkdir()
            (root / "docs" / "bootstrap-python.md").write_text("x", encoding="utf-8")
            with self.assertRaises(build_support.TitellaBuildError) as raised:
                build_support.documentation_sources(root)
            self.assertEqual(raised.exception.code, "docs_missing")

    def test_empty_oversized_and_non_utf8_documents_fail_the_build(self) -> None:
        cases = {
            "docs_invalid": [
                b"",
                b"\n" * (build_support.MAX_DOCUMENT_BYTES + 1),
                b"\xff\xfe not utf-8",
            ],
        }
        for code, payloads in cases.items():
            for payload in payloads:
                with tempfile.TemporaryDirectory() as temp:
                    root = Path(temp)
                    docs = root / "docs"
                    docs.mkdir()
                    for name in build_support.DOCUMENTATION_NAMES:
                        (docs / name).write_text("placeholder\n", encoding="utf-8")
                    (docs / build_support.DOCUMENTATION_NAMES[0]).write_bytes(payload)
                    with self.assertRaises(build_support.TitellaBuildError) as raised:
                        build_support.documentation_sources(root)
                    self.assertEqual(raised.exception.code, code)


class CheckoutFallbackContractTests(unittest.TestCase):
    """The loader must not synthesize a bundle from repository sources.

    This suite always resolves ``titella_slave_assets`` to the checkout copy
    (``tests/__init__.py`` puts ``src`` first on ``sys.path``), and the
    checkout carries no generated ``_data`` directory: serving a bundle from
    the repository itself is exactly what the artifact boundary forbids.
    """

    def test_load_bundle_has_no_checkout_fallback(self) -> None:
        with self.assertRaises(bundles.BundleError) as raised:
            bundles.load_bundle("python")
        self.assertEqual(raised.exception.code, bundles.BUNDLE_MISSING)


if __name__ == "__main__":
    unittest.main()
