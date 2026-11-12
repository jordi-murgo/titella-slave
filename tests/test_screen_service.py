"""Contract tests for the POSIX screen capture backends.

The backends are exercised without a graphical session: the tool invocations
are replaced by scripted ones, so the tests pin the contract that matters —
which tool is chosen, what a denial looks like, and that a silent
permission-less capture is never reported as a success.
"""
from __future__ import annotations

import os
import struct
import subprocess
import sys
import unittest
import zlib
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "implementations" / "python"))

import screen_service  # noqa: E402
from screen_service import (  # noqa: E402
    ScreenCaptureError,
    ScreenService,
    png_resolution,
)


def make_png(width: int = 64, height: int = 36) -> bytes:
    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    raw = bytearray()
    for row in range(height):
        raw.append(0)
        raw.extend(b"\x10\x20\x30" * width)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
        + chunk(b"IEND", b"")
    )


class Completed:
    def __init__(self, stdout: bytes = b"", stderr: bytes = b"", returncode: int = 0) -> None:
        self.stdout = stdout
        self.stderr = stderr
        self.returncode = returncode


class ScreenServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.png = make_png()
        self.saved_env = dict(os.environ)

    def tearDown(self) -> None:
        os.environ.clear()
        os.environ.update(self.saved_env)

    def force_session(self, kind: str) -> None:
        if kind == "macos":
            patcher = mock.patch.object(screen_service.os, "uname",
                                        return_value=mock.Mock(sysname="Darwin"))
        else:
            patcher = mock.patch.object(screen_service.os, "uname",
                                        return_value=mock.Mock(sysname="Linux"))
        patcher.start()
        self.addCleanup(patcher.stop)
        for key in ("XDG_SESSION_TYPE", "WAYLAND_DISPLAY", "DISPLAY"):
            os.environ.pop(key, None)
        if kind == "x11":
            os.environ["XDG_SESSION_TYPE"] = "x11"
        elif kind == "wayland":
            os.environ["XDG_SESSION_TYPE"] = "wayland"

    # -- macOS ---------------------------------------------------------------
    def test_macos_capture_reports_the_real_dimensions(self) -> None:
        self.force_session("macos")
        with mock.patch.object(screen_service, "_capture_macos",
                               return_value=(self.png, "macos-screencapture")):
            metadata, image = ScreenService().capture()
        self.assertEqual(metadata["width_px"], 64)
        self.assertEqual(metadata["height_px"], 36)
        self.assertEqual(metadata["bytes"], len(self.png))
        self.assertEqual(metadata["source"], "agent")
        self.assertEqual(metadata["monitor"], "primary")
        self.assertEqual(metadata["backend"], "macos-screencapture")
        self.assertEqual(image, self.png)

    def test_macos_without_screen_recording_permission_fails_loudly(self) -> None:
        """Without the grant the tool fails; the caller must see why."""
        self.force_session("macos")
        with mock.patch.object(screen_service, "macos_permission_granted", return_value=False), \
             mock.patch.object(screen_service, "_run") as run:
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._capture_macos()
        self.assertEqual(context.exception.code, "screen_permission_denied")
        self.assertFalse(context.exception.retryable)
        self.assertIn("Screen Recording", context.exception.message)
        run.assert_not_called()

    def test_macos_permission_stderr_is_mapped_to_the_real_cause(self) -> None:
        """macOS 26 prints this exact sentence instead of a TCC error code."""
        completed = Completed(stderr=b"could not create image from display\n", returncode=1)
        with mock.patch.object(screen_service.subprocess, "run", return_value=completed):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._run(["/usr/sbin/screencapture", "-x", "capture.png"])
        self.assertEqual(context.exception.code, "screen_permission_denied")
        self.assertFalse(context.exception.retryable)
        self.assertIn("Screen Recording", context.exception.message)

    def test_macos_unknown_permission_still_attempts_the_capture(self) -> None:
        """A platform that cannot be preflighted is not blocked on a guess."""
        self.force_session("macos")
        seen: list[list[str]] = []

        def fake_run(command, timeout=30):
            seen.append(list(command))
            target = command[-1]
            Path(target).write_bytes(self.png)
            return b""

        with mock.patch.object(screen_service, "macos_permission_granted", return_value=None), \
             mock.patch.object(screen_service, "_run", side_effect=fake_run):
            data, source = screen_service._capture_macos()
        self.assertEqual(data, self.png)
        self.assertEqual(source, "macos-screencapture")
        self.assertIn("/usr/sbin/screencapture", seen[0][0])
        self.assertIn("-m", seen[0])
        # The temporary image and its directory are gone: nothing is left on
        # the slave and the path never reuses a dot-prefixed name, which
        # screencapture refuses to write.
        target = seen[0][-1]
        self.assertFalse(os.path.exists(target))
        self.assertFalse(os.path.exists(os.path.dirname(target)))
        self.assertFalse(os.path.basename(target).startswith("."))

    # -- X11 -----------------------------------------------------------------
    def test_x11_prefers_import_and_returns_a_png(self) -> None:
        self.force_session("x11")
        calls: list[list[str]] = []

        def fake_run(command, timeout=30):
            calls.append(list(command))
            return self.png

        with mock.patch.object(screen_service.shutil, "which",
                               side_effect=lambda name: "/usr/bin/import" if name == "import" else None), \
             mock.patch.object(screen_service, "_run", side_effect=fake_run):
            data, source = screen_service._capture_x11()
        self.assertEqual(data, self.png)
        self.assertEqual(source, "x11-import")
        self.assertEqual(calls[0][0], "import")
        self.assertIn("root", calls[0])

    def test_x11_without_any_tool_is_unsupported_and_names_the_fix(self) -> None:
        self.force_session("x11")
        with mock.patch.object(screen_service.shutil, "which", return_value=None):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._capture_x11()
        self.assertEqual(context.exception.code, "screen_unsupported")
        self.assertFalse(context.exception.retryable)
        self.assertIn("ImageMagick", context.exception.message)

    def test_x11_xwd_without_a_converter_is_reported_not_faked(self) -> None:
        self.force_session("x11")

        def which(name):
            return "/usr/bin/xwd" if name == "xwd" else None

        with mock.patch.object(screen_service.shutil, "which", side_effect=which):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._capture_x11()
        self.assertEqual(context.exception.code, "screen_unsupported")
        self.assertIn("converter", context.exception.message)

    # -- Wayland -------------------------------------------------------------
    def test_wayland_without_gdbus_is_unsupported(self) -> None:
        self.force_session("wayland")
        with mock.patch.object(screen_service.shutil, "which", return_value=None):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._capture_wayland()
        self.assertEqual(context.exception.code, "screen_unsupported")
        self.assertIn("gdbus", context.exception.message)

    def test_wayland_denied_portal_is_a_permission_failure(self) -> None:
        """A denied or cancelled request must not look like a broken link."""
        self.force_session("wayland")

        def which(name):
            return "/usr/bin/gdbus" if name == "gdbus" else None

        with mock.patch.object(screen_service.shutil, "which", side_effect=which), \
             mock.patch.object(screen_service, "_portal_screenshot_uri", return_value=None):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._capture_wayland()
        self.assertEqual(context.exception.code, "screen_permission_denied")
        self.assertFalse(context.exception.retryable)
        self.assertIn("denied or cancelled", context.exception.message)

    def test_wayland_reads_and_removes_the_portal_image(self) -> None:
        self.force_session("wayland")
        import tempfile

        handle, path = tempfile.mkstemp(suffix=".png")
        os.close(handle)
        Path(path).write_bytes(self.png)

        def which(name):
            return "/usr/bin/gdbus" if name == "gdbus" else None

        with mock.patch.object(screen_service.shutil, "which", side_effect=which), \
             mock.patch.object(screen_service, "_portal_screenshot_uri",
                               return_value="file://" + path):
            data, source = screen_service._capture_wayland()
        self.assertEqual(data, self.png)
        self.assertEqual(source, "wayland-portal")
        self.assertFalse(os.path.exists(path), "the portal file is removed")

    # -- dispatch ------------------------------------------------------------
    def test_no_graphical_session_is_unsupported(self) -> None:
        self.force_session("headless")
        with self.assertRaises(ScreenCaptureError) as context:
            ScreenService().capture()
        self.assertEqual(context.exception.code, "screen_unsupported")
        self.assertIn("graphical session", context.exception.message)

    def test_oversized_image_is_refused(self) -> None:
        self.force_session("macos")
        big = self.png + b"\x00" * (screen_service.MAX_PNG_BYTES)
        with mock.patch.object(screen_service, "_capture_macos",
                               return_value=(big, "macos-screencapture")):
            with self.assertRaises(ScreenCaptureError) as context:
                ScreenService().capture()
        self.assertEqual(context.exception.code, "screen_too_large")
        self.assertFalse(context.exception.retryable)

    def test_non_png_output_is_a_capture_failure(self) -> None:
        self.force_session("macos")
        with mock.patch.object(screen_service, "_capture_macos",
                               return_value=(b"not a png at all", "macos-screencapture")):
            with self.assertRaises(ScreenCaptureError) as context:
                ScreenService().capture()
        self.assertEqual(context.exception.code, "screen_capture_failed")

    # -- shared tool wrapper -------------------------------------------------
    def test_missing_tool_is_unsupported(self) -> None:
        with mock.patch.object(screen_service.subprocess, "run",
                               side_effect=FileNotFoundError()):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._run(["definitely-not-installed"])
        self.assertEqual(context.exception.code, "screen_unsupported")
        self.assertFalse(context.exception.retryable)
        self.assertIn("definitely-not-installed", context.exception.message)

    def test_tool_failure_carries_its_real_diagnostic(self) -> None:
        with mock.patch.object(screen_service.subprocess, "run",
                               return_value=Completed(stderr=b"cannot open display", returncode=1)):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._run(["import", "root"])
        self.assertEqual(context.exception.code, "screen_capture_failed")
        self.assertTrue(context.exception.retryable)
        self.assertIn("cannot open display", context.exception.message)

    def test_tool_timeout_is_a_capture_failure(self) -> None:
        with mock.patch.object(screen_service.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired("import", 30)):
            with self.assertRaises(ScreenCaptureError) as context:
                screen_service._run(["import", "root"])
        self.assertEqual(context.exception.code, "screen_capture_failed")

    def test_png_resolution_rejects_non_png(self) -> None:
        self.assertIsNone(png_resolution(b"x" * 64))
        self.assertEqual(png_resolution(self.png), (64, 36))


class FileServiceScreenTests(unittest.TestCase):
    def test_screen_returns_metadata_and_bytes_and_maps_errors(self) -> None:
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "implementations" / "python"))
        from file_service import FileService, FileServiceError

        png = make_png()
        with mock.patch.object(screen_service.ScreenService, "capture",
                               return_value=({"format": "png", "bytes": len(png)}, png)):
            metadata, payload = FileService().dispatch("screen", {})
        self.assertEqual(metadata["format"], "png")
        self.assertEqual(payload, png)

        def refuse(self_):
            raise ScreenCaptureError("screen_permission_denied", "no permission", False)

        with mock.patch.object(screen_service.ScreenService, "capture", refuse):
            with self.assertRaises(FileServiceError) as context:
                FileService().dispatch("screen", {})
        self.assertEqual(context.exception.code, "screen_permission_denied")

    def test_unknown_operation_is_still_unknown(self) -> None:
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "implementations" / "python"))
        from file_service import FileService, FileServiceError

        with self.assertRaises(FileServiceError) as context:
            FileService().dispatch("teleport", {})
        self.assertEqual(context.exception.code, "unknown_op")


if __name__ == "__main__":
    unittest.main()
