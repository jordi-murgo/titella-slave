"""Regression test for SetupAPI's end-of-enumeration status on Windows."""
from __future__ import annotations

import ctypes
import importlib.util
import os
import unittest
from pathlib import Path
from unittest import mock

SOURCE = Path(__file__).resolve().parents[1] / "implementations/python/hid_transport.py"


class _Function:
    def __init__(self, callback=lambda *args: 1):
        self.callback = callback
        self.argtypes = None
        self.restype = None

    def __call__(self, *args):
        return self.callback(*args)


class _Library:
    def __init__(self, name, use_last_error):
        self.name = name
        self.use_last_error = use_last_error
        self.functions = {}
        self.destroyed = False

    def __getattr__(self, name):
        if name not in self.functions:
            if name == "SetupDiGetClassDevsW":
                callback = lambda *args: 123
            elif name == "SetupDiEnumDeviceInterfaces":
                # One valid matching interface, then ERROR_NO_MORE_ITEMS.
                callback = lambda _set, _device, _guid, index, _data: index == 0
            elif name == "SetupDiDestroyDeviceInfoList":
                def callback(*args):
                    self.destroyed = True
                    return 1
            else:
                callback = lambda *args: 1
            self.functions[name] = _Function(callback)
        return self.functions[name]


class WindowsHidDiscoveryTests(unittest.TestCase):
    def test_matching_collection_survives_end_of_enumeration(self):
        # Model ctypes' private last-error copy: it sees ERROR_NO_MORE_ITEMS
        # only when the WinDLL was loaded with use_last_error=True. Without the
        # flag, the same successful enumeration raises hid_not_found with 0.
        libraries = {}

        def load_library(name, use_last_error=False):
            libraries[name] = _Library(name, use_last_error)
            return libraries[name]

        spec = importlib.util.spec_from_file_location("hid_transport_windows_test", SOURCE)
        module = importlib.util.module_from_spec(spec)
        with mock.patch.object(os, "name", "nt"), \
             mock.patch.object(ctypes, "WinDLL", load_library, create=True), \
             mock.patch.object(ctypes, "get_last_error", lambda: 259 if libraries[
                 "setupapi.dll"].use_last_error else 0, create=True):
            spec.loader.exec_module(module)
            module._interface_path = lambda *_: "matching-hid-interface"
            module._probe = lambda *_: module._PROBE_EXACT
            self.assertEqual(module.find_data_collection("64E833579F30"),
                             "matching-hid-interface")
            self.assertTrue(libraries["setupapi.dll"].destroyed)


if __name__ == "__main__":
    unittest.main()
