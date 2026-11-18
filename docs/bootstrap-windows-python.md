# Bootstrap the Windows slave agent (Python, USB-only HID)

This guide covers the `windows-python` implementation: the same agent protocol
as the other variants, written in stdlib Python, running on Windows and talking
to the rig through the **HID data collection only** — no COM port, no
clipboard, no network, and nothing to install beyond a Python interpreter the
operator already has.

Use it when the target must stay as close to untouched as possible and a Python
3.9+ interpreter is already present, or when a `windows-dotnet` bootstrap is
refused by local policy (for example `Add-Type` or the framework compiler being
blocked) and the interpreter is not.

## Why a second Windows implementation

| | `windows-dotnet` | `windows-python` |
|---|---|---|
| Target requirement | PowerShell 5.1 + .NET Framework 4.7.2 | Python 3.9+ on `PATH` |
| Payload | C# source compiled at run time by `csc.exe` | deterministic zipapp |
| Transport | CDC or HID | HID only |
| Leaves on disk | `agent.exe` + `agent.json` in `%TEMP%` | `%TEMP%\titella-bootstrap-*` |
| Terminals | ConPTY | ConPTY (same mechanism) |
| Screen | GDI `CopyFromScreen` | GDI `BitBlt` |

Both implement the identical v1 wire protocol (`docs/serial-protocol.md`), the
same channels (`control`, `shell`, `fs`, `transfer`, `bootstrap`) and the same
error vocabulary, so the master cannot tell them apart except by the
`implementation` field of `agent.hello`.

## Target prerequisites

- Windows 10 version 1809 or later, or Windows 11.
- A Python interpreter on `PATH` as `python.exe` or `py.exe`, version 3.9 or
  later. The loader resolves it with `Get-Command` and fails with
  `python_unavailable` when neither is present; it never downloads or installs
  one.
- An interactive, unlocked desktop session: `fs.screen` captures the session
  the agent runs in, and `terminal.open` needs a pseudoconsole.
- The ESP32-S3 enumerated with the project identity: vendor id `0x17EF`,
  product id `0xA38F`, product `Titella KVM`, USB serial of twelve uppercase
  hexadecimal characters. The serial, not the port number, is the binding: the
  agent opens `HID:<serial>` and therefore cannot latch onto another device.
- The operator verified the target's active keyboard layout on the focused
  window for this attempt and passes it explicitly with
  `layout_confirmed=true`.

## Flow

1. `slave_bootstrap(owner, platform="windows", layout=..., layout_confirmed=true,
   skip_launch=true)` validates the bundle (size and SHA-256 of the payload, the
   loader, the stage-0 microloader and the transport unit) before any keystroke.
2. The master stages the compressed container in the firmware's Feature 0x11
   mailbox. It types a short launcher into the prepared console, presses Enter,
   and types the compressed Base64 stage-0 script as a separate line consumed
   by `[Console]::ReadLine()`. The console must already be open and focused.
3. Stage 0 pulls the container out of the mailbox with `HidD_GetFeature`,
   verifies its SHA-256, decompresses it, splits it on `#@#` and writes
   `hid_transport.py` plus the loader into `%TEMP%\<token>`. It starts the
   loader in the same console and waits for it to finish.

   Both input lines, including their Enter keys, share a 3328-character budget;
   each character costs 8 ms of HID pacing. Stage 0 discovers the device with
   `Get-PnpDevice` and declares only the four P/Invokes the mailbox needs. The
   composite device exposes several HID
   collections (relative mouse, keyboard, absolute mouse, vendor data), so the
   loop opens each one of the rig's and keeps the single one that answers the
   Feature 0x11 query with the expected token and size; a collection without
   that report either refuses the call or fails validation and is skipped.
4. The loader opens `HID:<serial>` itself, announces `bootstrap/loader.ready`,
   receives the zipapp in 512-byte `data` frames with per-frame `ack`, verifies
   size and SHA-256, answers `commit`, **closes the stream** and starts the
   agent.
5. The agent reopens the same collection under the named mutex
   `Local\Titella.Hid.<serial>`, announces `agent.hello` with
   `implementation="windows-python"`, and waits for `control.bind`.

The console shows `Loading loader ` with one dot per accepted mailbox block
(up to 44 bytes), then `Loading agent ` with one dot per newly written data
frame (up to 512 bytes).
Duplicates and retries do not add dots. Each phase ends in `OK` or `ERROR`.
The launcher can enter PowerShell history; the Base64 line is console input,
not a PowerShell command. It can still be echoed or recorded, and execution may
be observed by PowerShell logging, AMSI, transcription or other security tools.

Closing the stream between loader and agent is not cosmetic: the mutex is not
recursive across processes, so a loader that kept the handle would starve the
agent it just started.

## What the agent serves

- `control`: `bind`, `agent.stop`, heartbeats.
- `shell`: `job.start` / `job.cancel` through Windows PowerShell 5.1 with UTF-8
  forced; `terminal.open` / `terminal.resize` / `terminal.close` through ConPTY.
  Without pseudoconsole support `terminal.open` answers
  `terminal_unsupported` instead of pretending.
- `fs`: read, write, list, delete, replace, search, find, sysinfo and the
  on-demand screen capture. `uid`/`gid` are `null` on Windows: reporting a
  synthetic zero would claim a privilege level this process does not have.
- `transfer`: upload and download with exact size, SHA-256 and cancellation.

## Footprint and cleanup

- `%TEMP%\<token>` holds `hid_transport.py` and `loader.py` for the duration of
  the loader run; the loader deletes it once the payload is verified.
- `%TEMP%\titella-bootstrap-*` is the agent's own directory: the payload, the
  `owner.json` marker and the streams. The agent deletes it when the session
  ends (`agent.stop`) or when the grace window expires.
- Nothing is written outside `%TEMP%`, no service, scheduled task, registry
  key or firewall rule is created, and no elevation is requested.

## Failure codes

| Code | Meaning |
|---|---|
| `python_unavailable` | no `python.exe`/`py.exe` on `PATH`; the loader installs nothing |
| `hid_binding_required` | the typed loader received something other than `HID:<serial>` |
| `hid_not_found` | no vendor collection for that serial is present |
| `hid_ambiguous` | several collections claim the same serial |
| `hid_incompatible` | the collection does not expose 64-byte IN/OUT/Feature reports |
| `hid_busy` | another process holds `Local\Titella.Hid.<serial>` |
| `hid_open_timeout` | the OPEN handshake did not complete within 1 s |
| `hid_retry_exhausted` | the peer stopped acknowledging a control or data report |
| `hid_peer_closed` | the peer closed the session |
| `bootstrap_loader_timeout` | the stage-0 input did not run the loader |
| `agent_hello_timeout` | the payload verified but the agent never announced itself |

## Trust model

Unchanged from the other variants: the link is plaintext by an explicit
physical-trust decision. `link_id` correlates a session; it neither
authenticates nor encrypts. Any process able to open the vendor collection can
observe or inject operations with the permissions of the slave user.
