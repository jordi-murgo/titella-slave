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
- The ESP32-S3 native USB enumerated with product `Titella KVM` and serial
  `TITELLA-...`.

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
   explicit `slave_cdc_port` always wins. Without one, only a port whose PnP
   instance id carries the Espressif vendor id (`VID_303A`), the `TITELLA-`
   serial or the `Titella KVM` product name is accepted: zero candidates is
   `cdc_not_found`, several is `cdc_ambiguous`, and an unrelated COM device is
   never opened or written to. Windows may label the CDC `USB Serial Device`
   when no driver package carries the descriptor, so the friendly name alone
   is not a reliable signal. The port is opened exclusively
   (`CreateFileW("\\\\.\\COMx", shareMode=0)`), 115200 8N1, no flow control.
   It reads frames with a bounded 4096-byte line buffer (4097-byte buffer,
   discard up to LF on overflow), not with an unbounded `ReadLine`.

   The loader polls `BytesToRead` and sleeps 15 ms while the port is idle
   rather than relying on a serial read timeout: the timeout surfaces as a
   wrapped `MethodInvocationException` whose shape varies between hosts, while
   `BytesToRead` never throws on an idle port.
5. The loader announces `loader.ready`. The master sends the payload in
   512-byte blocks with per-block ACK; duplicates are re-acknowledged but not
   applied twice, and gaps are never written or acknowledged.
6. The loader verifies size and SHA-256, moves the payload in the bootstrap
   directory, then starts a **detached child PowerShell** with
   `-NoLogo -NoProfile -NonInteractive -EncodedCommand` and one short
   command: `Add-Type -LiteralPath <payload.cs> -OutputAssembly <agent.dll>
   -ReferencedAssemblies System.dll,System.Core.dll,System.Web.Extensions.dll,<GAC
   System.Management.Automation.dll>` followed by
   `[Reflection.Assembly]::LoadFrom(<agent.dll>)` and
   `[Titella.Slave.Agent]::Run(...)`. The loader closes the CDC before that
   child starts and the child reopens the same port. The on-disk DLL lets
   every later job child load the compiled agent for the in-process
   Runspace runner.
7. The child compiles the payload and enters `Agent.Run`, which calls
   `FreeConsole`: closing the original console window does not stop the agent.
   `Agent.Run` receives the bootstrap directory, the `link_id`, the resolved
   port, a 5000 ms heartbeat and a 30000 ms grace window.
8. The agent emits `control.hello` with `implementation: "windows-dotnet"`,
   `runtime: {name: "dotnet-framework", version: <NDP Version>}`,
   `python: null`, `uid: null`, the real `pid` and `agent: "titella-slave"`.
   The master sends `control.bind` and reports success only after
   `status=ready`.

## CDC port resolution

The override wins: `slave_cdc_port` (argument, then configuration) must match
`COM[1-9][0-9]*`; anything else is `invalid_argument` before any keystroke.

Without an override the loader inspects `Get-PnpDevice -Class Ports` and keeps
the present ports whose own PnP instance id carries the Espressif vendor id
(`VID_303A`), a `TITELLA-` serial or the `Titella KVM` product name. The vendor
id is the durable signal: Windows labels the CDC `USB Serial Device` when no
driver package carries the firmware descriptor, so a name-only match misses a
healthy device.

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

## Timing

| Stage | Wait |
|---|---|
| Launcher (Start search) and stub typing | 8 ms per character; each HID request is at most 256 characters. The stub is about 80 characters, so the typed stage is under a second. The loader arrives over the CDC. |
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
- `shell.job.start/cancel`, output events and results; each job is a child
  powershell.exe assigned to a job object (cancellation and timeouts kill
  the whole tree), but the command line carries no user script: it only
  loads the compiled agent DLL and runs the in-process Runspace runner
  (`JobEntry`), while the script itself travels over stdin and executes
  in a custom `PSHost`. This keeps the script text out of every classic
  PowerShell log channel (Id 400/800 `HostApplication`, 4103/4104 script
  text, PSReadLine history) while keeping full PowerShell semantics:
  stdout and stderr are captured separately, `exit 7` returns 7 and any
  cmdlet error returns 1, exactly like the previous `-EncodedCommand`
  child;
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
| `cdc_not_found` | No `Titella KVM` CDC was identified, or PnP identity is unavailable. Pass `slave_cdc_port`. | Yes, with a correct port. |
| `cdc_ambiguous` | Several Titella CDC ports are present. Pass `slave_cdc_port`. | Yes, with a correct port. |
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
