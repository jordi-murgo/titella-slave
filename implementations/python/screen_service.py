"""One on-demand screenshot of the local graphical session.

Three backends, selected by what the session actually offers, never by the
operating system name alone:

- macOS: the system ``screencapture`` tool. Without the Screen Recording
  permission it fails with exit status 1 and "could not create image from
  display" (measured on macOS 26 with a launchd-parented process). The
  CoreGraphics preflight via ctypes is what turns that opaque sentence into
  ``screen_permission_denied`` before the tool is even launched, so the caller
  learns the real cause instead of parsing a stderr line.
- X11: ``import`` (ImageMagick) or ``xwd`` piped through a converter. Neither
  is guaranteed to be installed, so a missing tool is an error that names it.
- Wayland: the freedesktop portal through ``gdbus``, which is the only
  supported path because a Wayland compositor does not expose a global
  framebuffer. The portal may require the user to confirm each capture; a
  denied or cancelled request is reported as ``screen_permission_denied``.

The result is always one PNG of the primary monitor plus its dimensions. No
file is written to disk, nothing runs between calls, and this module never
imports a third-party package: the slave payload is stdlib-only.
"""
from __future__ import print_function

import os
import shutil
import struct
import subprocess
import time

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
# The encoded image ceiling shared with the master receiver; a larger capture
# is refused here instead of being streamed and rejected later.
MAX_PNG_BYTES = 32 * 1024 * 1024
CAPTURE_TIMEOUT_S = 30

ERROR_CAPTURE_FAILED = "screen_capture_failed"
ERROR_PERMISSION_DENIED = "screen_permission_denied"
ERROR_UNSUPPORTED = "screen_unsupported"
ERROR_TOO_LARGE = "screen_too_large"


class ScreenCaptureError(RuntimeError):
    def __init__(self, code, message, retryable=True):
        RuntimeError.__init__(self, message)
        self.code = code
        self.message = message
        self.retryable = retryable


def png_resolution(data):
    """(width, height) of a PNG, or None when the bytes are not one."""
    if len(data) < 24 or data[:8] != PNG_SIGNATURE or data[12:16] != b"IHDR":
        return None
    width, height = struct.unpack(">II", data[16:24])
    return (width, height) if width > 1 and height > 1 else None


def _run(command, timeout=CAPTURE_TIMEOUT_S):
    """Runs a capture command and returns its output, or raises honestly."""
    tool = os.path.basename(command[0])
    try:
        completed = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
        )
    except FileNotFoundError:
        raise ScreenCaptureError(
            ERROR_UNSUPPORTED,
            "the screenshot tool '%s' is not installed on the slave" % tool,
            retryable=False,
        )
    except OSError as exc:
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED,
            "the screenshot tool '%s' cannot be executed: %s" % (tool, exc),
        )
    except subprocess.TimeoutExpired:
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED,
            "the screenshot tool '%s' did not finish in %d s" % (tool, timeout),
        )
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", errors="replace").strip()
        if "could not create image from display" in detail:
            # The exact sentence macOS prints when the process has no Screen
            # Recording grant (measured on macOS 26). Reported as the real
            # cause rather than as a generic tool failure.
            raise ScreenCaptureError(
                ERROR_PERMISSION_DENIED,
                "macOS refused the capture: this process has no Screen Recording "
                "permission. Grant it to the application that launches the agent "
                "(Terminal, iTerm or the terminal used for the bootstrap) in "
                "System Settings > Privacy & Security > Screen Recording, then "
                "bootstrap again",
                retryable=False,
            )
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED,
            "the screenshot tool '%s' failed (%d): %s"
            % (tool, completed.returncode, detail[:2000] or "no diagnostic"),
        )
    return completed.stdout


def _session_kind():
    """Which graphical stack this process can capture from."""
    if os.uname().sysname == "Darwin":
        return "macos"
    session_type = os.environ.get("XDG_SESSION_TYPE", "").strip().lower()
    if session_type == "wayland" or os.environ.get("WAYLAND_DISPLAY"):
        return "wayland"
    if session_type == "x11" or os.environ.get("DISPLAY"):
        return "x11"
    return "unknown"


def macos_permission_granted():
    """Whether this process may record the screen, or None when unknowable.

    ``CGPreflightScreenCaptureAccess`` is asked through ctypes because the
    payload cannot depend on pyobjc. It answers for the process's responsible
    application: a process launched from Terminal inherits Terminal's grant,
    while the same binary started by launchd (no responsible app) reports
    False and cannot capture at all. The check runs first so the failure is
    named instead of surfacing as screencapture's "could not create image from
    display".
    """
    try:
        import ctypes
        import ctypes.util

        library = ctypes.util.find_library("CoreGraphics")
        if not library:
            return None
        core = ctypes.CDLL(library)
        preflight = getattr(core, "CGPreflightScreenCaptureAccess", None)
        if preflight is None:
            return None
        preflight.restype = ctypes.c_bool
        preflight.argtypes = []
        return bool(preflight())
    except (ImportError, OSError, AttributeError):
        return None


def _capture_macos():
    granted = macos_permission_granted()
    if granted is False:
        raise ScreenCaptureError(
            ERROR_PERMISSION_DENIED,
            "macOS has not granted Screen Recording to this process: allow the "
            "terminal that launched the agent in System Settings > Privacy & "
            "Security > Screen Recording, then bootstrap again",
            retryable=False,
        )
    # screencapture writes a file, never stdout (verified: -t png - exits 0
    # with no output), and it refuses a destination whose name starts with a
    # dot ("cannot write file to intended destination"), so the temporary is
    # created with an ordinary name and removed in the finally block.
    import tempfile as _tempfile

    directory = _tempfile.mkdtemp(prefix="titella-screen-")
    path = os.path.join(directory, "capture.png")
    try:
        _run(["/usr/sbin/screencapture", "-x", "-t", "png", "-m", path])
        try:
            with open(path, "rb") as stream:
                data = stream.read()
        except OSError as exc:
            raise ScreenCaptureError(
                ERROR_CAPTURE_FAILED, "the capture file cannot be read: %s" % exc
            )
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass
        try:
            os.rmdir(directory)
        except OSError:
            pass
    if not data:
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED, "screencapture produced no image data"
        )
    return data, "macos-screencapture"


def _capture_x11():
    # ImageMagick first: one process, PNG straight from the root window.
    if shutil.which("import"):
        return _run(["import", "-silent", "-window", "root", "png:-"]), "x11-import"
    if shutil.which("xwd"):
        # xwd has no PNG encoder; it needs a converter, and shipping a
        # half-pipeline that fails later would be worse than naming the gap.
        converter = next(
            (name for name in ("convert", "magick", "ffmpeg") if shutil.which(name)),
            None,
        )
        if converter is None:
            raise ScreenCaptureError(
                ERROR_UNSUPPORTED,
                "xwd is installed but no converter (convert, magick or ffmpeg) "
                "is available to turn its output into a PNG",
                retryable=False,
            )
        if converter in ("convert", "magick"):
            return (
                _run([converter, "xwd:-", "png:-"]),
                "x11-xwd",
            )
        return (
            _run([
                "ffmpeg", "-hide_banner", "-loglevel", "error",
                "-f", "x11grab", "-i", os.environ.get("DISPLAY", ":0") + ".0+0,0",
                "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-",
            ]),
            "x11-ffmpeg",
        )
    raise ScreenCaptureError(
        ERROR_UNSUPPORTED,
        "no X11 screenshot tool is installed: install ImageMagick (import) or "
        "x11-apps (xwd) on the slave",
        retryable=False,
    )


def _capture_wayland():
    """Captures through the freedesktop portal, the only Wayland path.

    The portal answers with a file URI, not with bytes, so the capture is read
    back and removed. A denial or a cancellation is reported as a permission
    failure instead of an empty image: on Wayland the user is part of the
    capture, and the caller must see that.
    """
    gdbus = shutil.which("gdbus")
    if gdbus is None:
        raise ScreenCaptureError(
            ERROR_UNSUPPORTED,
            "the Wayland screenshot portal needs gdbus; install glib2 (gdbus) "
            "on the slave",
            retryable=False,
        )
    uri = _portal_screenshot_uri(gdbus)
    if not uri:
        raise ScreenCaptureError(
            ERROR_PERMISSION_DENIED,
            "the Wayland screenshot portal returned no file: the request was "
            "denied or cancelled. The portal may ask the user to confirm each "
            "capture.",
            retryable=False,
        )
    if not uri.startswith("file://"):
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED,
            "the portal answered with an unsupported reference %r" % uri[:200],
        )
    path = uri[len("file://"):]
    try:
        with open(path, "rb") as stream:
            data = stream.read()
    except OSError as exc:
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED, "the portal image cannot be read: %s" % exc
        )
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass
    return data, "wayland-portal"


def _portal_screenshot_uri(gdbus):
    """One Screenshot call on the portal, returning the file URI or None."""
    call = [
        gdbus, "call", "--session",
        "--dest", "org.freedesktop.portal.Desktop",
        "--object-path", "/org/freedesktop/portal/desktop",
        "--method", "org.freedesktop.portal.Screenshot.Screenshot",
        "", "{}",
    ]
    handle = None
    try:
        output = _run(call).decode("utf-8", errors="replace")
    except ScreenCaptureError:
        raise
    # The call returns the request handle; the result arrives as a signal on
    # the same connection, which a one-shot gdbus call cannot see. Ask the
    # portal for the result with a bounded monitor instead.
    for token in output.replace("(", " ").replace(")", " ").replace(",", " ").split():
        text = token.strip().strip("'\"")
        if text.startswith("/org/freedesktop/portal/desktop/request/"):
            handle = text
            break
    if handle is None:
        return None
    return _portal_result(gdbus, handle)


def _portal_result(gdbus, handle):
    """Waits for the portal's Response signal and extracts the URI."""
    monitor = [
        gdbus, "monitor", "--session",
        "--dest", "org.freedesktop.portal.Desktop",
        "--object-path", handle,
    ]
    try:
        process = subprocess.Popen(
            monitor, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
    except OSError:
        return None
    deadline = time.monotonic() + CAPTURE_TIMEOUT_S
    try:
        while time.monotonic() < deadline:
            line = process.stdout.readline()
            if not line:
                break
            text = line.decode("utf-8", errors="replace")
            if "Response" not in text:
                continue
            if "'file://" in text:
                start = text.index("file://")
                end = text.find("'", start)
                return text[start:] if end < 0 else text[start:end]
            # An empty reference means the user refused or cancelled.
            return None
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
    return None


class ScreenService(object):
    """Serves ``fs.screen`` for the Python agent."""

    def __init__(self):
        self._last_source = None

    def source(self):
        return self._last_source or _session_kind()

    def capture(self):
        """Returns (metadata, png bytes) for the primary monitor."""
        kind = _session_kind()
        if kind == "macos":
            image, source = _capture_macos()
            monitor = "primary"
        elif kind == "x11":
            image, source = _capture_x11()
            monitor = "primary"
        elif kind == "wayland":
            image, source = _capture_wayland()
            monitor = "primary"
        else:
            raise ScreenCaptureError(
                ERROR_UNSUPPORTED,
                "no graphical session was detected: neither a Wayland "
                "compositor nor an X11 display is reachable from this process. "
                "The agent captures the session it runs in, so it must be "
                "launched inside the logged-in desktop.",
                retryable=False,
            )
        resolution = png_resolution(image)
        if resolution is None:
            raise ScreenCaptureError(
                ERROR_CAPTURE_FAILED,
                "the %s backend did not return a valid PNG" % source,
            )
        if len(image) > MAX_PNG_BYTES:
            raise ScreenCaptureError(
                ERROR_TOO_LARGE,
                "the encoded PNG is %d bytes, above the %d byte ceiling"
                % (len(image), MAX_PNG_BYTES),
                retryable=False,
            )
        self._last_source = source
        metadata = {
            "format": "png",
            "width_px": resolution[0],
            "height_px": resolution[1],
            "bytes": len(image),
            "source": "agent",
            "monitor": monitor,
            "backend": source,
        }
        return metadata, image
