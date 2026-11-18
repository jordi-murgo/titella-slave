"""On-demand screenshot of the interactive Windows desktop.

The Windows counterpart of ``screen_service.py`` and the Python port of the
C# ``ScreenService``: one PNG of the primary monitor at its physical pixel
size, origin (0,0), captured with the permissions of the agent's own session.
Nothing is written to disk, nothing runs between calls, and the module is
stdlib-only (``ctypes`` only, no pywin32, no PIL).

The capture is best effort by nature: a locked session, a secure desktop
(UAC), a protected surface or a disconnected RDP session is not guaranteed to
be capturable. A failure is reported with its real diagnostic (Win32 error
number plus system message); a valid black PNG is not evidence of any
particular cause, so no failure path ever returns a black frame as success.

The PNG is encoded by hand over ``zlib`` (signature, IHDR, one filtered IDAT,
IEND, CRC-32 per chunk): 8-bit RGB, filter 0 per scanline. The per-monitor-v2
DPI awareness context is set on the calling thread only for the duration of
the call, exactly like the C# implementation, so DPI virtualization cannot
shrink the capture, and the caller's context is restored afterwards.

Importing this module on a non-Windows system must not raise: every Win32
library handle is loaded lazily, on first capture.
"""
from __future__ import print_function

import ctypes
import struct
import sys
import zlib

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
# The encoded image ceiling shared with the master receiver; a larger capture
# is refused here instead of being streamed and rejected later.
MAX_PNG_BYTES = 32 * 1024 * 1024

ERROR_CAPTURE_FAILED = "screen_capture_failed"
ERROR_PERMISSION_DENIED = "screen_permission_denied"
ERROR_UNSUPPORTED = "screen_unsupported"
ERROR_TOO_LARGE = "screen_too_large"

# The backend named in the capture metadata.
BACKEND = "gdi-bitblt"

# Win32 constants used below.
_SM_CXSCREEN = 0
_SM_CYSCREEN = 1
_BI_RGB = 0
_SRCCOPY = 0x00CC0020
_DIB_RGB_COLORS = 0
_ERROR_ACCESS_DENIED = 5
_FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000
_FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200
# SetThreadDpiAwarenessContext pseudo-handle for DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
_DPI_PER_MONITOR_AWARE_V2 = -4


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


class _BITMAPINFOHEADER(ctypes.Structure):
    """The BITMAPINFOHEADER passed to GetDIBits; layout is fixed by Win32."""

    _fields_ = [
        ("biSize", ctypes.c_uint32),
        ("biWidth", ctypes.c_int32),
        ("biHeight", ctypes.c_int32),
        ("biPlanes", ctypes.c_uint16),
        ("biBitCount", ctypes.c_uint16),
        ("biCompression", ctypes.c_uint32),
        ("biSizeImage", ctypes.c_uint32),
        ("biXPelsPerMeter", ctypes.c_int32),
        ("biYPelsPerMeter", ctypes.c_int32),
        ("biClrUsed", ctypes.c_uint32),
        ("biClrImportant", ctypes.c_uint32),
    ]


# Filled by _libraries() on first capture; None before that, and forever on a
# non-Windows system, so importing the module stays side-effect free.
_user32 = None
_gdi32 = None
_kernel32 = None


def _libraries():
    """Loads user32/gdi32/kernel32 lazily, with prototypes, or raises honestly."""
    global _user32, _gdi32, _kernel32
    if _user32 is not None:
        return
    if sys.platform != "win32":
        raise ScreenCaptureError(
            ERROR_UNSUPPORTED,
            "this screen service captures the Windows desktop through GDI; "
            "it cannot run under %s" % sys.platform,
            retryable=False,
        )
    try:
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    except (OSError, AttributeError) as error:
        raise ScreenCaptureError(
            ERROR_UNSUPPORTED,
            "the Windows graphics libraries could not be loaded: %s" % error,
            retryable=False,
        )

    # SetThreadDpiAwarenessContext only exists from Windows 10 1703 on; the
    # prototypes are attached where the API is present so the pseudo-handle
    # survives the call as a full pointer, and _enter_per_monitor_v2() has a
    # documented fallback for the systems that predate it.
    setter = getattr(user32, "SetThreadDpiAwarenessContext", None)
    if setter is not None:
        setter.argtypes = [ctypes.c_void_p]
        setter.restype = ctypes.c_void_p
    user32.SetProcessDPIAware.argtypes = []
    user32.SetProcessDPIAware.restype = ctypes.c_int
    user32.GetSystemMetrics.argtypes = [ctypes.c_int]
    user32.GetSystemMetrics.restype = ctypes.c_int
    user32.GetDC.argtypes = [ctypes.c_void_p]
    user32.GetDC.restype = ctypes.c_void_p
    user32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    user32.ReleaseDC.restype = ctypes.c_int

    gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
    gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
    gdi32.CreateCompatibleBitmap.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    gdi32.CreateCompatibleBitmap.restype = ctypes.c_void_p
    gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    gdi32.SelectObject.restype = ctypes.c_void_p
    gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
    gdi32.DeleteObject.restype = ctypes.c_int
    gdi32.DeleteDC.argtypes = [ctypes.c_void_p]
    gdi32.DeleteDC.restype = ctypes.c_int
    gdi32.BitBlt.argtypes = [
        ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_uint32,
    ]
    gdi32.BitBlt.restype = ctypes.c_int
    gdi32.GetDIBits.argtypes = [
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_void_p, ctypes.POINTER(_BITMAPINFOHEADER), ctypes.c_uint32,
    ]
    gdi32.GetDIBits.restype = ctypes.c_int

    kernel32.FormatMessageW.argtypes = [
        ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_void_p,
    ]
    kernel32.FormatMessageW.restype = ctypes.c_uint32

    _user32 = user32
    _gdi32 = gdi32
    _kernel32 = kernel32


def _win32_message(code):
    """The FormatMessageW text for a Win32 error code, without the trailing CRLF."""
    if code <= 0:
        return None
    buffer = ctypes.create_unicode_buffer(512)
    length = _kernel32.FormatMessageW(
        _FORMAT_MESSAGE_FROM_SYSTEM | _FORMAT_MESSAGE_IGNORE_INSERTS,
        None,
        code,
        0,  # user default language
        buffer,
        len(buffer),
        None,
    )
    if length == 0:
        return None
    return buffer.value.rstrip("\r\n") or None


def _win32_error():
    """The last Win32 error as (number, readable message or None)."""
    code = ctypes.get_last_error()
    return code, _win32_message(code)


def _raise_failure(action, code, message):
    """One honest capture failure carrying everything that is known about it."""
    detail = "%s failed" % action
    if message:
        detail += ": %s" % message
    if code and code > 0:
        detail += " (Win32 error %d)" % code
    if code == _ERROR_ACCESS_DENIED:
        raise ScreenCaptureError(ERROR_PERMISSION_DENIED, detail)
    raise ScreenCaptureError(ERROR_CAPTURE_FAILED, detail)


def _fail(action, code=None, message=None):
    """Raises a failure for ``action``; by default with the last Win32 error."""
    if code is None:
        code, message = _win32_error()
    _raise_failure(action, code, message)


def _enter_per_monitor_v2():
    """Sets per-monitor-v2 awareness on this thread; returns what to restore.

    Returns the previous context to restore afterwards, or None when the OS
    predates SetThreadDpiAwarenessContext and the process-wide
    SetProcessDPIAware is the only available way to see physical pixels.
    """
    setter = getattr(_user32, "SetThreadDpiAwarenessContext", None)
    if setter is None:
        if not _user32.SetProcessDPIAware():
            _fail("SetProcessDPIAware", code=0)
        return None
    previous = setter(_DPI_PER_MONITOR_AWARE_V2)
    if not previous:
        _fail("SetThreadDpiAwarenessContext")
    return previous


def _restore_dpi_context(previous):
    """Restores the saved context; a failure only matters when nothing else did.

    Restoring a different context would silently rescale the next capture, so
    a failed restore is reported as a capture failure unless another error is
    already on its way out (the same rule the C# finally block applies).
    """
    if not previous:
        return
    if not _user32.SetThreadDpiAwarenessContext(previous):
        if sys.exc_info()[0] is None:
            _fail("restoring the previous thread DPI awareness context")


def _primary_size():
    """Physical pixel size of the primary monitor, or an honest failure."""
    width = _user32.GetSystemMetrics(_SM_CXSCREEN)
    height = _user32.GetSystemMetrics(_SM_CYSCREEN)
    pixels = width * height
    if width <= 0 or height <= 0 or pixels <= 0 or pixels > 0x7FFFFFFF:
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED,
            "the primary monitor reports no usable area (%dx%d)" % (width, height),
        )
    return width, height


def _bitblt_rgb(width, height):
    """Copies the primary monitor to a top-down RGB buffer via GDI BitBlt.

    Raises ScreenCaptureError with the real Win32 diagnostic on any failure;
    there is no success path that skips BitBlt or GetDIBits, so a black frame
    cannot come out of here as if the capture had worked.
    """
    screen_dc = _user32.GetDC(None)
    if not screen_dc:
        _fail("GetDC")
    memory_dc = None
    bitmap = None
    previous = None
    try:
        memory_dc = _gdi32.CreateCompatibleDC(screen_dc)
        if not memory_dc:
            _fail("CreateCompatibleDC")
        bitmap = _gdi32.CreateCompatibleBitmap(screen_dc, width, height)
        if not bitmap:
            _fail("CreateCompatibleBitmap")
        previous = _gdi32.SelectObject(memory_dc, bitmap)
        if not previous:
            _fail("SelectObject")
        # Source (0,0) is the primary monitor's origin; the whole monitor is
        # copied in one call, and a zero return is a real failure.
        if not _gdi32.BitBlt(
            memory_dc, 0, 0, width, height,
            screen_dc, 0, 0,
            _SRCCOPY,
        ):
            _fail("BitBlt")
        # GetDIBits must not be given a bitmap that is still selected into a
        # device context, so the DC gets its previous object back first.
        if not _gdi32.SelectObject(memory_dc, previous):
            _fail("SelectObject", code=0,
                  message="the device context's previous bitmap could not be restored")
        previous = None

        header = _BITMAPINFOHEADER()
        header.biSize = ctypes.sizeof(_BITMAPINFOHEADER)
        header.biWidth = width
        header.biHeight = -height  # negative: top-down, first scanline on top
        header.biPlanes = 1
        header.biBitCount = 24
        header.biCompression = _BI_RGB
        # A DIB row is padded out to a 32-bit boundary; exactly width * 3
        # bytes of each row are copied out below.
        stride = ((width * 3 + 3) // 4) * 4
        buffer = ctypes.create_string_buffer(stride * height)
        if _gdi32.GetDIBits(
            memory_dc, bitmap, 0, height, buffer, ctypes.byref(header),
            _DIB_RGB_COLORS,
        ) != height:
            _fail("GetDIBits")

        rows = []
        for row in range(height):
            start = row * stride
            # A DIB stores blue, green, red whatever the compression; PNG
            # colour type 2 needs red, green, blue, so the triplets swap.
            line = bytearray(buffer.raw[start:start + width * 3])
            line[0::3], line[2::3] = line[2::3], line[0::3]
            rows.append(bytes(line))
        return b"".join(rows)
    finally:
        # The bitmap must not be deleted while still selected into the DC.
        if previous:
            _gdi32.SelectObject(memory_dc, previous)
        if bitmap:
            _gdi32.DeleteObject(bitmap)
        if memory_dc:
            _gdi32.DeleteDC(memory_dc)
        _user32.ReleaseDC(None, screen_dc)


def _encode_png(width, height, rgb):
    """Writes a PNG by hand: 8-bit RGB, filter 0 per scanline, one IDAT."""
    if len(rgb) != width * height * 3:
        raise ScreenCaptureError(
            ERROR_CAPTURE_FAILED,
            "the captured pixel buffer is %d bytes for a %dx%d RGB image"
            % (len(rgb), width, height),
        )

    def chunk(tag, payload):
        body = tag + payload
        return (
            struct.pack(">I", len(payload)) + body +
            struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
        )

    raw = bytearray()
    for row in range(height):
        raw.append(0)  # filter type 0 (None) on every scanline
        start = row * width * 3
        raw.extend(rgb[start:start + width * 3])
    return (
        PNG_SIGNATURE +
        chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) +
        chunk(b"IDAT", zlib.compress(bytes(raw), 6)) +
        chunk(b"IEND", b"")
    )


def _metadata(width, height, size):
    """The metadata dictionary, with the same keys and meaning as the POSIX one."""
    return {
        "format": "png",
        "width_px": width,
        "height_px": height,
        "bytes": size,
        "source": "agent",
        "monitor": "primary",
        "backend": BACKEND,
    }


class ScreenService(object):
    """Serves ``fs.screen`` for the Windows Python agent."""

    def capture(self):
        """Returns (metadata, png bytes) for the primary monitor."""
        _libraries()
        previous = _enter_per_monitor_v2()
        try:
            width, height = _primary_size()
            rgb = _bitblt_rgb(width, height)
        finally:
            _restore_dpi_context(previous)
        png = _encode_png(width, height, rgb)
        if len(png) > MAX_PNG_BYTES:
            raise ScreenCaptureError(
                ERROR_TOO_LARGE,
                "the encoded PNG is %d bytes, above the %d byte ceiling"
                % (len(png), MAX_PNG_BYTES),
                retryable=False,
            )
        if png_resolution(png) != (width, height):
            raise ScreenCaptureError(
                ERROR_CAPTURE_FAILED,
                "the encoder produced a PNG whose IHDR disagrees with the "
                "captured area (%dx%d)" % (width, height),
            )
        return _metadata(width, height, len(png)), png
