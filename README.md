# titella-slave

Slave side of the Titella KVM auditing rig: implementations of the v1 serial
protocol that run on the controlled machine. The repository hosts one
interchangeable implementation per supported runtime; the Python
implementation for macOS and Linux is included here.

The agent is ephemeral: it is transferred over HID + CDC by the master, runs
with the current user's privileges, installs nothing, opens no sockets and
deletes its own payload and bootstrap directory when the session ends.

## Layout

- `implementations/python/` — stdlib-only Python agent (entry point
  `__main__.py`); requires Python >= 3.9, no packages.
- `implementations/windows/` — C# units for .NET Framework 4.7.2+ on
  Windows 10 1809+/11; no Python, Node, Java, SDK or modern .NET is needed.
- `src/titella_slave_assets/wire.py` — canonical v1 wire codec shared by every
  implementation.
- `src/titella_slave_assets/bundles.py` — `load_bundle()` validation of the
  packaged artifacts against their manifest.
- `build_support.py` — deterministic generation of `titella-slave.pyz`,
  `titella-slave.cs`, both loaders, the Windows HID bootstrap artifacts
  (`windows-hid-stage0.ps1.tmpl`, `windows-hid-transport.cs`) and
  `manifest.json` into `build_lib/titella_slave_assets/_data/` via the
  setuptools `build_py` hook. `windows-hid-transport.cs` is a byte copy of
  the sole `implementations/windows/HidByteStream.cs`; no second source copy
  exists.
- `loaders/python.py.tmpl` — loader typed over HID on the target; it locates the
  CDC, receives the payload with per-block ACK, checks size and SHA-256 and
  starts the zipapp.
- `loaders/windows.ps1.tmpl` — PowerShell 5.1 loader typed over HID on the
  target; it resolves the COM port from the PnP identity (or an explicit
  override), receives the C# payload with per-block ACK, checks size and
  SHA-256 and starts a detached child that compiles and runs the agent.
- `docs/serial-protocol.md` — canonical v1 contract: framing and limits,
  envelope, allowed directions, ACK, transfer window, heartbeat, reconnection
  and the agent attachment gate.
- `docs/bootstrap-python.md` — Python 3.9+ on macOS and Linux: prerequisites,
  loader handshake, footprint, cleanup and error codes.
- `docs/bootstrap-windows.md` — Windows 10 1809+/11 with PowerShell 5.1 and
  .NET Framework 4.7.2+: COM resolution, C# payload compilation, ConPTY, job
  containment, timeouts and policy refusals.

The three documents are copied by the build hook into
`titella_slave_assets/_data/docs/`, so the master serves them over MCP
(`docs_index`, `docs_read`, `titella://docs/<id>`) straight from the installed
wheel; their canonical source stays in `docs/` here and in the sdist.

## Build

```bash
python3 -m build --outdir dist
```

The wheel `titella-slave-assets==1.1.0` is stdlib-only: no runtime
dependencies and `Requires-Python >=3.9`. Payloads are byte-reproducible;
the manifest (schema 2) pins size and SHA-256 of every artifact, including
the Windows `hid_bootstrap` block, and `load_bundle()` re-validates them at
load time.

## Tests

```bash
python3 -m unittest discover -s tests -t . -v
```

The suite exercises the codec, the deterministic build, the manifest and
bundle validation, the file, shell and transfer services and the real
zipapp over a PTY, using only in-suite protocol peers — the master
repository is not needed to run it.

## Trust model

The link is plaintext by an explicit physical-trust decision. `link_id`
correlates a session and allows resuming a short replug; it neither
authenticates nor encrypts. Any process with access to the CDC or UART can
observe or inject operations with the permissions of the slave user.
