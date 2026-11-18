# Bootstrap the Windows slave agent (PowerShell 5.1, .NET Framework)

This guide covers the C# implementation of the slave agent, used when the
master selects `platform="windows"`. The target needs no Python, Node, Java,
`dotnet`, SDK or NuGet package: the payload is C# source compiled at run time
by the system PowerShell with the in-box .NET Framework compiler.

## Target prerequisites

- Windows 10 version 1809 or later, or Windows 11.
- Windows PowerShell 5.1 (`%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe`).
- .NET Framework 4.7.2 or later: the loader reads
  `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` and requires
  `Release >= 461808`.
- An interactive, unlocked desktop session: the bootstrap opens Start search
  and launches a console PowerShell there.
- The operator verified the target's active keyboard layout on the focused
  input window for this attempt and passes it explicitly with
  `layout_confirmed=true`. `slave_bootstrap` rejects a missing, unsupported or
  unconfirmed layout with `slave_layout_required`, `invalid_argument` or
  `slave_layout_unconfirmed` before any keystroke; the configured
  `keyboard_layout` is never a fallback.
- The ESP32-S3 native USB enumerated with the project identity: vendor id
  `0x17EF`, product id `0xA38F`, product `Titella KVM` and a serial of twelve
  uppercase hexadecimal characters.

The loader never changes the execution policy, never uses
`-ExecutionPolicy Bypass`, never calls `Unblock-File`, never asks for
elevation, never downloads anything and never creates a service, scheduled
task or exclusion. If a policy blocks `FullLanguage`, the framework version or
`Add-Type`, the bootstrap fails visibly. There is no hidden alternative path.

## Flow

1. `slave_bootstrap(owner, platform="windows", layout=..., layout_confirmed=true)`
   loads `titella-slave.cs` from the installed `titella-slave-assets` wheel and
   validates it against the manifest (size and SHA-256) before any keystroke.
   The layout must name the input source verified on the focused window of this
   attempt.
2. HID sends `CTRL`+`ESC`, which opens Start without depending on the Windows
   key (desktop software can intercept it), types `powershell` into Start
   search and presses `ENTER`. Start search launches the system Windows
   PowerShell 5.1.

   Windows can apply input sources per window. This call opens Start search
   and then a new console, and types the stub without a pause for inspection.
   Before attesting the layout, establish that the same input source will
   apply to both windows; an earlier screenshot, taskbar indicator or OS UI
   language cannot prove the future PowerShell window's layout. If per-window
   input sources might differ and this cannot be established, stop.

   An earlier profile used `CMD`+`R` for the Run dialog. Two facts proved that
   path fragile and it is no longer used: `kb_key` resolves a one-character key
   name through an ASCII to HID mapping, so an uppercase `R` carries a synthetic
   Shift and produces `Win+Shift+R` with no dialog; and the Run dialog keeps the
   previous command in its field, so a typed launcher is appended to stale text.
   Start search has neither problem because every keystroke lands in an empty
   search box.
3. The loader itself does **not** travel over the keyboard. The master types a
   compact PowerShell stub that opens the CDC port (`$script:Sp`) with `DTR`
   and `RTS` asserted, sends a protocol v1 `bootstrap` `event`
   (`op="stage.ready"`), and reads the compressed loader (`windows.ps1.tmpl`)
   over the stateless `SerialMux` in 510-byte `bootstrap` `data` frames with
   per-frame `ack` flow control until `op="stage.done"`. The stub decompresses
   the GZip stream in memory and dot-sources the `ScriptBlock` while keeping
   `$script:Sp` open, so the firmware stays 100 % stateless and `COMx` is
   opened only once in PowerShell.
4. The loader checks its environment gates and resolves the CDC port. An
   explicit `slave_cdc_port` always wins. Without one, only a present port
   whose PnP instance id carries the project vendor/product pair
   (`VID_17EF&PID_A38F`) is accepted: zero candidates is `cdc_not_found`,
   several is `cdc_ambiguous`, and an unrelated COM device is never opened or
   written to. Windows may label the CDC `USB Serial Device` when no driver
   package carries the descriptor, so the friendly name alone is not a
   reliable signal. The port is opened exclusively
   (`CreateFileW("\\\\.\\COMx", shareMode=0)`), 2,000,000 8N1, no flow control.
   It reads frames with a bounded 4096-byte line buffer (4097-byte buffer,
   discard up to LF on overflow), not with an unbounded `ReadLine`.

   The loader polls `BytesToRead` and sleeps 15 ms while the port is idle
   rather than relying on a serial read timeout: the timeout surfaces as a
   wrapped `MethodInvocationException` whose shape varies between hosts, while
   `BytesToRead` never throws on an idle port.
5. The loader announces `loader.ready`. The master sends the payload in
   512-byte blocks with per-block ACK; duplicates are re-acknowledged but not
   applied twice, and gaps are never written or acknowledged.
6. The loader verifies size and SHA-256 and moves the payload into the
   bootstrap directory, then compiles it with the framework's own `csc.exe`
   and the same reference set the payload has always needed. The compile mode
   follows the selected implementation: `windows-dotnet` produces
   **agent.exe** (`/target:exe /main:Titella.Slave.AgentHost`) started
   directly, while `windows-dotnet-dll` produces **agent.dll**
   (`/target:library`) which a separate Windows PowerShell 5.1 process loads
   from disk and invokes. In both cases the source is deleted after a
   successful compile, the fixed `agent.json` is written next to the assembly,
   and no `-EncodedCommand` or script text reaches a classic PowerShell log
   channel. A compile failure keeps `agent-compile.err.log` and is reported
   through the loader's error flow. The loader closes the CDC before the agent
   starts; the agent reopens the same port.
7. The agent reads `agent.json` and enters `Agent.Run`, which calls
   `FreeConsole`: closing the original console window does not stop the agent.
   `agent.json` carries the bootstrap directory, the `link_id`, the resolved
   port, a 5000 ms heartbeat and a 30000 ms grace window; the DLL variant adds
   `implementation: "windows-dotnet-dll"`, and the assembly kind decides which
   value is accepted — an exe rejects that field unless it says
   `windows-dotnet`, and a library rejects anything else, including its
   absence. Later job children are launched from the same assembly: agent.exe
   started with `--job`, or the hosting PowerShell process loading agent.dll
   and calling `AgentHost.Main(['--job'])`. The user script still arrives over
   stdin, so it never appears in the child's command line.
8. The agent emits `control.hello` with the implementation it was started as
   (`windows-dotnet` or `windows-dotnet-dll`),
   `runtime: {name: "dotnet-framework", version: <NDP Version>}`,
   `python: null`, `uid: null`, the real `pid` and `agent: "titella-slave"`.
   The master sends `control.bind` and reports success only after
   `status=ready`.

The DLL variant exists to measure the same agent where an executable image is
refused, not as a stealth mode: it adds a PowerShell host process and the
telemetry that comes with it, and a reflection load is not the image-loading
path a policy engine validates. Whether a host admits it is decided by that
host's controls and observed there.

Observed on one hardened Windows 11 host (HP EliteBook, WDAC `DefaultWindowsAudit`
`{e0abda1f-ccf0-468e-8855-3e0f08b02d6a}` in audit mode, Defender ASR enabled):

- **Admission.** CodeIntegrity `3076` recorded `powershell.exe` loading the
  generated `agent.dll` and said "the image was allowed to load" **because the
  policy is auditing**. No ASR `1121`/`1122` was raised for the DLL or for any
  job. A host with that policy enforced, or with AppLocker DLL rules, decides
  the opposite — the event is the evidence, not the successful bootstrap.
- **Telemetry the exe path does not have.** Every job child starts with
  `-Command "[void][Reflection.Assembly]::LoadFrom('…\agent.dll'); exit
  ([Titella.Slave.AgentHost]::Main([string[]]@('--job')))"`. With PowerShell
  script-block logging on, that literal is written to event `4104` once per
  job, with the full DLL path. This is a real detectability regression
  relative to `windows-dotnet`, which puts only `--job` on a child command
  line.
- **Containment survives.** Timeout and cancellation return
  `0xC000013A` (TerminateJobObject), the grandchild stops writing and no
  `ping.exe` survives; the exit code propagates (`exit 7` → 7) and stdout
  carries only the script output.

So the variant is a measurement instrument for admission decisions, not a
quieter payload. Report it that way.

## CDC port resolution

The override wins: `slave_cdc_port` (argument, then configuration) must match
`COM[1-9][0-9]*`; anything else is `invalid_argument` before any keystroke.

**Clipboard rigs.** With `agent_protocol = "clipboard"` the master renders the
sentinel `CLIPBOARD` instead of a port. No COM port is resolved and no serial
device is opened: the loader sends `loader.ready` and every later frame inside
`TITELLA_S2M` packets written with `Set-Clipboard`, and polls `Get-Clipboard
-Raw` every 20 ms for `TITELLA_M2S` packets, feeding each line through the same
`begin`/`data`/`commit` handlers the serial path uses. One S2M packet carries
all the acknowledgements produced by one master packet, and a re-asserted
master packet puts the last S2M packet back on the clipboard. The commit
response is written before the loader exits, and `Agent.Run` then receives
`CLIPBOARD` as its port and opens the shared-clipboard transport. A host that
defines `$script:CbRx`/`$script:CbTx` hooks keeps them: the loader prefers those
over the cmdlets. Packet format, sequencing and acknowledgement rules are in
[the clipboard CDC section](serial-protocol.md#clipboard-cdc-agent_protocol--clipboard).

Without an override the loader inspects `Get-PnpDevice -PresentOnly -Class
Ports` and keeps the present ports whose own PnP instance id carries the
project vendor/product pair `VID_17EF&PID_A38F`. That pair is the durable
signal: Windows labels the CDC `USB Serial Device` when no driver package
carries the firmware descriptor, so a name-only match misses a healthy device,
and the Espressif vendor id alone no longer identifies this rig.

| Outcome | Result |
|---|---|
| exactly one candidate | that port is used |
| zero candidates | `cdc_not_found`, with the explicit-override instruction |
| several candidates | `cdc_ambiguous`, listing them and asking for `slave_cdc_port` |
| PnP identity unavailable | `cdc_not_found`; an explicit `slave_cdc_port` is required |

The loader never picks the first COM port and never infers a VID/PID. The
agent receives the port already resolved and reuses it when the link
reconnects, as the Python agent does.

If firmware changes the USB identity, Windows may assign a different COM
number. Re-run the bootstrap with the current port; a stale override is
reported honestly, not silently retried on another port.

## HID data transport

`slave_transport = "hid"` carries the same protocol v1 frames over the
vendor-defined HID collection of the device instead of a COM port, the
shared clipboard or the network. The binding the loader hands to
`Agent.Run` is `HID:<serial>`: exactly the twelve uppercase hexadecimal
characters of the USB serial number of the vendor data collection. The
binding is never a COM port, an interface number or a friendly name.

The transport unit (`HidByteStream`) is a full-duplex peer of the firmware
endpoint. Frames travel in **report 0x10** reports of 64 bytes including
the report ID: version, class (`OPEN`, `OPEN_ACK`, `DATA`, `ACK`, `CLOSE`),
a non-zero `uint32` epoch that correlates one connection, a `uint16`
sequence per direction and a payload of at most 54 bytes. One DATA report
is in flight per direction; an acknowledgement is only sent after the
receiver admitted every payload byte into its 8192-byte queue, so a full
queue applies backpressure instead of losing or truncating data. Exact
duplicates are re-acknowledged without being applied twice, out-of-sequence
reports are dropped without an acknowledgement, control frames overtake
data, and sequence numbers wrap modulo 65536. DATA and OPEN retransmit on a
250 ms deadline with three retries before the link fails explicitly. A
graceful close sends `CLOSE` for its own epoch; a stale epoch never touches
the current session.

Collection selection uses USB identity and capabilities, never enumeration
order: `HidD_GetAttributes` must report VID `0x17EF` and PID `0xA38F`, the
serial string must equal the binding, and `HidP_GetCaps` must report usage
page `0xFF00`, usage `0x01` and 64-byte IN, OUT and Feature report lengths.
Keyboard and mouse collections of the same device are ignored; a data
collection with the right identity and wrong lengths is `hid_incompatible`.
The device is opened with `FILE_FLAG_OVERLAPPED` and a non-inheritable
handle, and a `Local\Titella.Hid.<serial>` mutex keeps a second consumer
from opening the same collection.

### USB-only bootstrap

A HID rig has no CDC, no shared clipboard and no network reach, so the
bootstrap types a short launcher and a separate Base64 line, then pulls the
loader and agent over USB:

1. The master stages the compressed loader container in the firmware's
   Feature 0x11 mailbox over the UART (`loader_stage_begin`,
   `loader_stage_chunk`, `loader_stage_commit`): chunks of at most 512
   bytes, at most 32768 committed bytes, SHA-256 checked at commit, and the
   snapshot expires 180 s after commit. The container is the rendered
   loader, the `#@#` marker and the `HidByteStream` unit as raw UTF-8: no
   Base64 transport source is ever registered as script text.
2. The master types a short launcher and then the gzip+Base64 stage-0 script
   into the prepared console. The launcher reads the second line with
   `[Console]::ReadLine()` and runs it. Both lines and their Enter keys must
   fit 3328 characters (~26.6 s of pacing). The stage-0 loader enumerates the
   present HID interfaces with SetupDi and
   keeps only paths carrying the rig identity (VID `0x17EF`, PID `0xA38F`);
   it admits the one whose metadata answers the expected token, version and
   size, downloads the snapshot in 44-byte blocks (four attempts per block,
   30 s budget), verifies SHA-256 **before** decompressing, splits the
   container on `#@#`, compiles the `HidByteStream` unit from a temporary
   file (`Add-Type` registers the path, not the unit text) and runs the
   loader part in the same console. It never spawns a process, reads the
   clipboard or opens a COM port.
3. The loader reuses the precompiled `HidByteStream` type, opens
   `HID:<serial>`, announces `loader.ready` over the IN/OUT data transport
   and receives `begin`/`data`/`commit` exactly as the CDC path does.
4. On `commit` the loader disposes the stream (handle and mutex released) and
   starts the detached agent process; the agent reopens the binding with a new
   epoch. The loader returns to the console instead of exiting, so the same
   window accepts another bootstrap, and a failed attempt also leaves it
   alive.
5. Before creating its own directory the loader sweeps tombstones of previous
   bootstraps: a bare 16-hex-digit directory under TEMP that carries
   `owner.json` naming a dead pid is deleted; anything else is never touched.
   On stop the agent empties its directory except the running image and
   `owner.json`, which only the next bootstrap's sweep can remove.

The console prints one dot for each newly accepted mailbox block (up to 44
bytes), then for each newly written agent block (up to 512 bytes). Retries and
duplicates add no dots. Each transfer ends with `OK` or `ERROR`. The launcher
may be saved in PowerShell history; console echo, transcription, AMSI and
script-block logging remain separate exposure paths.

The stage-0 helper only reads Feature reports of candidates whose metadata
matches the staged snapshot; it never changes another device. A snapshot that
exceeds 32768 bytes or combined stage-0 input over 3328 characters fails the
bootstrap before any keystroke.
The CDC and clipboard rigs above keep their current behaviour; HID is selected
per instance by configuration, never by an automatic fallback.

A stopped HID agent is proven gone by `hid.health` reporting
`slave_connected: false` for the destination the firmware currently forwards
to — `cdc` or `hid` — never by a stop reply or its timeout: the transport may
still be attached. Both values are meaningful: the HID endpoint closes with
its epoch, and the core's CDC `operator bool` follows the host `DTR && RTS`
line state (`_onLineState`), so it drops when the host closes the port rather
than merely on unplug. The bootstrap waits for that state before it changes
the firmware's destination or types anything, so a still-open stream — or an
unobservable one — surfaces as `agent_stop_timeout` instead of a silent
`hid_busy` later.

| Code | Meaning |
|---|---|
| `invalid_argument` | The binding is not `HID:<12 uppercase hex>`. |
| `hid_stage_not_found` | No reachable HID interface publishes the staged token and size. |
| `hid_ambiguous` | Several HID devices publish the staged snapshot. |
| `hid_stage_timeout` | The 30 s snapshot budget was exceeded. |
| `hid_stage_failed` | A block exhausted its four attempts without a valid reply. |
| `checksum_mismatch` | The downloaded snapshot does not match the declared SHA-256; nothing was executed. |
| `hid_not_found` | No present HID data collection carries the serial. |
| `hid_incompatible` | The collection does not expose 64-byte IN/OUT/Feature reports. |
| `hid_busy` | Another Titella consumer holds the collection mutex. |
| `hid_open_timeout` | The OPEN handshake did not complete within 1000 ms. |
| `hid_retry_exhausted` | Three retries of OPEN or DATA went unanswered. |
| `hid_write_timeout` | A native write did not complete within 250 ms. |
| `hid_peer_closed` | The peer closed the current epoch. |
| `hid_io_error` | The device failed while reading or writing reports. |
| `hid_session_reset` | A new epoch discarded bytes that were still unacknowledged. |
| `hid_closed` | The stream was disposed while a caller was blocked. |

## Timing

| Stage | Wait |
|---|---|
| Launcher (Start search) and stub typing | 8 ms per character; each HID request is at most 256 characters. The stub is about 80 characters, so the typed stage is under a second. The loader arrives over the CDC. |
| HID stage-0 input | Launcher, Base64 line and two Enter keys: at most 3328 characters at 8 ms per character, plus request overhead and execution. This is a budget, not a measured bootstrap time. |
| HID snapshot download | 44-byte blocks, four attempts per block, 30 s total budget. |
| `loader.ready` after the launcher | 30 s |
| `control.hello` after the payload commit | 90 s (including the C# compile) |
| Loader bootstrap deadline | 180 s |
| CDC open retries within the grace window | 0.5 s, 1 s, 2 s, 5 s |
| Heartbeat | 5 s |
| Disconnect grace window | 30 s |

Compilation happens in the child process, never inside the `commit` response:
`commit` returns once the payload is stored, and the wait for `hello` covers
the compile.

## Services of the Windows agent

The agent implements the full protocol v1 surface, not a reduced subset:

- `fs.read/write/list/delete/replace/search/find/sysinfo` and the
  `fs` stream for payloads above 2048 bytes;
- `upload.begin/commit`, `download.begin`, `transfer.status/cancel` and the
  transfer events, with the same limits as the Python agent (read 32 KiB
  default / 64 KiB maximum, write 64 KiB, list 200, search 200, find 500,
  transfer 512 MiB, window 1..64 with a default of 8);
- `shell.job.start/cancel`, output events and results; each job is the agent
  assembly itself started with `--job` and assigned to a job object
  (cancellation and timeouts kill the whole tree). The command line carries
  no user script: `--job` boots the in-process Runspace runner (`JobEntry`)
  inside the agent, while the script itself travels over stdin and executes
  in a custom `PSHost`. Under `windows-dotnet` the child is `agent.exe`; under
  `windows-dotnet-dll` it is the PowerShell host that loads `agent.dll` and
  calls the same entry point, so the DLL variant adds one process per job that
  the exe variant does not have. The script remains subject to PowerShell
  engine logging and endpoint security policy inside the Runspace.
  PowerShell semantics are preserved: stdout and stderr are captured
  separately, `exit 7` returns 7 and any cmdlet error returns 1, exactly like
  a `powershell.exe -Command` child;
- `terminal.open/write/resize/close` over ConPTY (`CreatePseudoConsole`,
  `STARTUPINFOEX`, `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`,
  `CreateProcessW`, `ResizePseudoConsole`) with the system PowerShell 5.1 as
  the default shell. Output and input are drained by different threads; the
  output queue is 256 KiB and terminal chunks are 512 bytes, matching the
  master's offsets and ring buffer. VT output from ConPTY is preserved; it is
  not byte-identical to a Unix PTY.

`search` is a literal, case-sensitive search, not a regular expression.
`replace` requires exactly one UTF-8 match. `find` applies fnmatch-style
patterns to names (not PowerShell provider globbing), sorts pages ordinally,
and does not traverse junctions or reparse points. `delete` is not recursive
and does not remove ordinary directories; it may delete a link without
following its target. `~` expands to the user profile; arbitrary variables are
never executed.

## Process containment

Every job and terminal owns a Job Object created with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The child is created suspended, assigned
to its Job before `ResumeThread`; if the assignment fails the child is
terminated and the call fails with `process_containment_failed` — there is no
fallback to killing a single PID. Cancellation and timeout call
`TerminateJobObject`: Windows has no POSIX signals and the agent does not
simulate them or promise a graceful shutdown.

The shared capacity is **4** processes across jobs and terminals. The fifth
call receives the existing capacity error and starts nothing. Job timeout is
1..3600 s, terminal rows/columns are 1..1000, and the idle timeout is
1..3600 s.

`slave_bootstrap` on Windows reports `ready` only after verifying that ConPTY
and process containment are available; a platform without those capabilities
returns an error instead of a partially functional agent.

## Cleanup

On `slave_stop`, on a failed bootstrap, or after the 30 s grace window, the
agent:

1. rejects new operations;
2. terminates its job trees and cancels transfers;
3. closes the CDC;
4. deletes its payload and its own `titella-bootstrap-*` directory under the
   user's `TEMP`;
5. exits with a real exit code.

The main thread stays alive until cleanup finishes; the heartbeat thread only
signals expiration. Only directories this loader family created are removed;
user history, external logs and unrelated directories are never touched. If
the loader fails before the CDC opens, the only evidence is the console
message on the target and `bootstrap_loader_timeout` on the master; the master
does not invent a remote error it could not receive.

## Errors

| Code | Meaning | Retry |
|---|---|---|
| `slave_layout_required` | No explicit `layout`; configuration is never a bootstrap fallback. | Yes, after verifying the focused window's input source. |
| `slave_layout_unconfirmed` | `layout` passed without a real `layout_confirmed=true` for this attempt. | Yes, after a fresh verification. |
| `invalid_argument` | `slave_cdc_port` is not `COM[1-9][0-9]*`, or the bootstrap arguments are invalid. | No, until corrected. |
| `cdc_not_found` | No present CDC with the project identity (`VID_17EF&PID_A38F`) was identified, or PnP identity is unavailable. Pass `slave_cdc_port`. | Yes, with a correct port. |
| `cdc_ambiguous` | Several Titella CDC ports are present. Pass `slave_cdc_port`. | Yes, with a correct port. |
| `slave_transport_unsupported` | `slave_transport=hid` was selected but the flashed firmware does not advertise the HID data protocol (older than 1.6.0), or the bundle carries no stage-0 microloader. | No fallback; reflash or switch the selection. |
| `hid_loader_too_large` | The compressed loader exceeds the 32768-byte firmware snapshot. | No; shrink the loader. |
| `hid_stage0_too_large` | The typed stage-0 input exceeds the 3328-character keystroke budget. | No; shrink the microloader. |
| `agent_stop_timeout` | A stop was sent but `hid.health` never reported `slave_connected: false` for the selected destination within the wait. The old stream may still be attached. | Yes, once the state proves closed (or after checking the agent/console). |
| `bootstrap_loader_timeout` | The loader did not announce `loader.ready`: launcher focus, a policy block before the port opens, or a port that cannot be opened. | Yes, after fixing the named cause. |
| `cdc_disconnected` | The loader lost the CDC while reading frames (unplugged device or port reset) before the commit. | Yes, after checking the cable and the port. |
| `size_mismatch` / `checksum_mismatch` | The received payload does not match size or SHA-256; the temporary is removed. | Yes, from scratch. |
| `agent_hello_timeout` | The payload started but no `control.hello` arrived within 90 s; typically a compile error. Inspect `agent-compile.err.log` in the bootstrap directory. | Yes, after fixing the compile cause. |
| `process_containment_failed` | The child could not be assigned to its Job Object; the child was terminated. | Yes, after fixing the environment. |
| `takeover_denied` | A different `link_id` while the agent is alive. | No, unless the previous agent terminates. |

## Observability

The agent is observable by Windows Defender, the EDR and the audit policy in
force. It does not manipulate telemetry, history, AMSI, WDAC or AppLocker,
does not add exclusions, and does not suppress alerts. Any alert observed
during authorized testing is preserved as evidence. Diagnostics are written
only to files inside its own bootstrap directory.
