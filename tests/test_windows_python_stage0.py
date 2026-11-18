"""Regression coverage for retrying the Windows Python HID stage-zero loader."""
from __future__ import annotations

import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE0 = ROOT / "loaders" / "windows-python-hid-stage0.ps1.tmpl"


class WindowsPythonStage0Tests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("pwsh"), "PowerShell is not installed")
    def test_stale_previous_type_does_not_poison_new_declarations(self) -> None:
        # The previous stage-zero defined T.H with short method names. PowerShell
        source = STAGE0.read_text(encoding="utf-8")
        declarations = (source.split("$dev = [IntPtr]::Zero; $n = 0", 1)[0]
                        .replace("__HID_TOKEN__", "0123456789ABCDEF")
                        .replace("__HID_SIZE__", "1")
                        .replace("__HID_SHA256__", "a" * 64))
        script = r'''
$ErrorActionPreference = 'Stop'
Add-Type -Name H -Namespace T -MemberDefinition 'public static int C() { return 0; }'
$source = [Console]::In.ReadToEnd()
& ([ScriptBlock]::Create($source))
& ([ScriptBlock]::Create($source))
$type = 'Titella.PythonHidStage0' -as [type]
if (-not $type) { throw 'stage-zero interop type missing' }
foreach ($name in 'CreateFileW','CloseHandle','HidD_GetFeature','HidD_SetFeature') {
    $method = $type.GetMethod($name)
    if (-not $method) { throw "interop method missing: $name" }
    $attribute = [Runtime.InteropServices.DllImportAttribute]::GetCustomAttribute(
        $method, [Runtime.InteropServices.DllImportAttribute])
    if (-not $attribute -or $attribute.EntryPoint -ne $name) {
        throw "wrong native entry point: $name"
    }
}
'''
        completed = subprocess.run(
            [shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-Command", script],
            input=declarations, text=True, capture_output=True, timeout=30, check=False,
        )
        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)


if __name__ == "__main__":
    unittest.main()
