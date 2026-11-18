"""Exercise accepted-block progress without a physical HID device."""
from __future__ import annotations

import ast
import base64
import contextlib
import hashlib
import io
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class PythonLoaderProgressTests(unittest.TestCase):
    def test_new_data_advances_once_duplicates_and_gaps_do_not(self) -> None:
        source = (ROOT / "loaders/windows-python-hid.py.tmpl").read_text()
        tree = ast.parse(source)
        handler = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "hframe")
        acknowledgements = []
        output = io.BytesIO()
        scope = {
            "base64": base64, "out": output, "expected": 0, "S": "session",
            "send": lambda *args, **kwargs: acknowledgements.append(kwargs["ack_seq"]),
        }
        exec(compile(ast.Module(body=[handler], type_ignores=[]), "loader", "exec"), scope)
        def frame(seq, data):
            return {"type": "data", "session_id": "session", "seq": seq, "data_b64": data}
        screen = io.StringIO()
        with contextlib.redirect_stdout(screen):
            scope["hframe"](frame(0, base64.b64encode(b"first").decode()))
            scope["hframe"](frame(0, base64.b64encode(b"first").decode()))
            scope["hframe"](frame(2, base64.b64encode(b"gap").decode()))
            with self.assertRaises(Exception):
                scope["hframe"](frame(1, "!invalid!"))
            scope["hframe"](frame(1, base64.b64encode(b"second").decode()))
        self.assertEqual(output.getvalue(), b"firstsecond")
        self.assertEqual(acknowledgements, [0, 0, 1])
        self.assertEqual(screen.getvalue(), "..")


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell is not installed")
class DotnetLoaderProgressTests(unittest.TestCase):
    def test_new_data_advances_once_duplicates_and_gaps_do_not(self) -> None:
        source = (ROOT / "loaders/windows.ps1.tmpl").read_text()
        handler = "function Handle-Frame($f) {" + source.split("function Handle-Frame($f) {", 1)[1].split("\nfunction Start-Agent", 1)[0]
        script = r'''
$script:Wr = [IO.MemoryStream]::new(); $script:Sq = 0; $script:S = 'session'; $script:L = 'link'; $script:Hid = $true
$script:acks = [Collections.Generic.List[int]]::new()
function Send($frame) { [void]$script:acks.Add($frame.ack_seq) }
''' + handler + r'''
function Frame($seq, $data) { return @{type='data';session_id='session';seq=$seq;data_b64=$data} }
Handle-Frame (Frame 0 'Zmlyc3Q=')
Handle-Frame (Frame 0 'Zmlyc3Q=')
Handle-Frame (Frame 2 'Z2Fw')
try { Handle-Frame (Frame 1 '!invalid!'); throw 'invalid data accepted' } catch { if ($_.Exception.Message -eq 'invalid data accepted') { throw } }
Handle-Frame (Frame 1 'c2Vjb25k')
[Console]::Write('|RESULT:' + [Text.Encoding]::UTF8.GetString($script:Wr.ToArray()) + ':' + ($script:acks -join ','))
'''
        result = subprocess.run(
            [shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-Command", script],
            text=True, capture_output=True, timeout=30, check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "..|RESULT:firstsecond:0,0,1")


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell is not installed")
class MailboxProgressTests(unittest.TestCase):
    def test_progress_counts_only_accepted_blocks_and_reports_checksum_error(self) -> None:
        data = bytes(range(100))
        for filename, namespace, name in (
            ("windows-python-hid-stage0.ps1.tmpl", "Titella", "PythonHidStage0"),
            ("windows-hid-stage0.ps1.tmpl", "Titella", "HW"),
        ):
            for valid in (True, False):
                with self.subTest(loader=filename, valid=valid):
                    source = (ROOT / "loaders" / filename).read_text()
                    source = source.split("$ms = [IO.MemoryStream]::new($buf)", 1)[0]
                    digest = hashlib.sha256(data).hexdigest() if valid else "0" * 64
                    source = (source.replace("__HID_TOKEN__", "0123456789ABCDEF")
                                    .replace("__HID_SIZE__", str(len(data)))
                                    .replace("__HID_SHA256__", digest))
                    fake = r'''
using System;
public static class PLACEHOLDER {
 public static byte[] Data, Token; private static uint offset; private static int attempts;
 public static string[] Paths() { return new[]{"fake"}; }
 public static IntPtr CreateFileW(string p, uint a, uint s, IntPtr z, uint d, uint f, IntPtr t) { return new IntPtr(1); }
 public static bool CloseHandle(IntPtr h) { return true; }
 public static bool HidD_SetFeature(IntPtr h, byte[] b, uint n) { offset=BitConverter.ToUInt32(b,12); attempts++; return true; }
 public static bool HidD_GetFeature(IntPtr h, byte[] b, uint n) {
  Array.Clear(b,0,b.Length); b[0]=17; b[1]=1; b[2]=(byte)(attempts==0 ? 1 : 2);
  Array.Copy(Token,0,b,4,8); Array.Copy(BitConverter.GetBytes((uint)Data.Length),0,b,16,4);
  if(attempts>0) { int len=Math.Min(44,Data.Length-(int)offset); b[3]=(byte)len;
   Array.Copy(BitConverter.GetBytes(attempts%2==1 ? offset+1 : offset),0,b,12,4);
   Array.Copy(Data,(int)offset,b,20,len); }
  return true;
 }
}
'''.replace("PLACEHOLDER", name)
                    setup = (
                        "Add-Type -Namespace '" + namespace + "' -Name '" + name + "' -MemberDefinition @'\n"
                        # MemberDefinition needs members, not a complete class declaration.
                        + fake.split("{", 1)[1].rsplit("}", 1)[0] + "\n'@\n"
                        + "[" + namespace + "." + name + "]::Data=[byte[]](0..99)\n"
                        + "[" + namespace + "." + name + "]::Token=[byte[]](1,35,69,103,137,171,205,239)\n"
                        + "function Get-PnpDevice { [pscustomobject]@{InstanceId='HID\\VID_17EF&PID_A38F\\fake'} }\n"
                    )
                    result = subprocess.run(
                        [shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-Command", setup + source],
                        text=True, capture_output=True, timeout=30, check=False,
                    )
                    self.assertEqual(result.returncode == 0, valid, result.stderr)
                    self.assertEqual(result.stdout.strip(), "Loading loader ... " + ("OK" if valid else "ERROR"))
