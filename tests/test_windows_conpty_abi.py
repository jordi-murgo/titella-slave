"""Regression tests for the Windows ConPTY COORD calling convention."""
from __future__ import annotations

import ctypes
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "implementations/python"))
import shell_service_win as win  # noqa: E402


class _Api:
    def __init__(self):
        self.created_size = None
        self.resized_size = None

    def CreatePseudoConsole(self, size, _input, _output, _flags, result):
        if not isinstance(size, win.COORD):
            raise TypeError("ConPTY requires COORD passed by value")
        self.created_size = (size.X, size.Y)
        ctypes.cast(result, ctypes.POINTER(win._HANDLE))[0] = win._HANDLE(99)
        return 0

    def CreateProcessW(self, *args):
        info = ctypes.cast(args[-1], ctypes.POINTER(win.PROCESS_INFORMATION)).contents
        info.hProcess = 123
        info.hThread = 124
        return True

    def ResizePseudoConsole(self, _handle, size):
        if not isinstance(size, win.COORD):
            raise TypeError("ConPTY requires COORD passed by value")
        self.resized_size = (size.X, size.Y)
        return 0

    def DeleteProcThreadAttributeList(self, _buffer):
        pass


class _Job:
    def assign_and_resume(self, _process, _thread):
        pass

    def close(self):
        pass


class ConptyCallingConventionTests(unittest.TestCase):
    def test_launch_and_resize_pass_coordinates_by_value(self):
        api = _Api()
        manager = win.ShellSessionManager(None, win.ProcessRegistry())
        try:
            with mock.patch.object(win, "_conpty_api", return_value=api), \
                 mock.patch.object(win, "_win32", return_value=api), \
                 mock.patch.object(win, "_create_pipe", side_effect=[(1, 2), (3, 4)]), \
                 mock.patch.object(win, "_close_handle"), \
                 mock.patch.object(win, "_ProcessJob", _Job), \
                 mock.patch.object(manager, "_pseudoconsole_attribute_list",
                                   return_value=ctypes.create_string_buffer(8)):
                terminal = manager._launch("powershell.exe", None, 24, 80, 900)
                self.assertEqual(api.created_size, (80, 24))
                manager.sessions[terminal.session_id] = terminal
                result = manager.resize(terminal.session_id, 30, 100)
                self.assertEqual(api.resized_size, (100, 30))
                self.assertEqual((result["rows"], result["cols"]), (30, 100))
                manager.sessions.clear()
        finally:
            manager.stop_event.set()


if __name__ == "__main__":
    unittest.main()
