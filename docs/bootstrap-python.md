# Bootstrap the Python slave agent (macOS, Linux)

This guide covers the Python implementation of the slave agent, used when the
master selects `platform="macos"` or `platform="linux"`. Windows uses the C#
implementation and its own PowerShell loader; see `bootstrap-windows`. The
master never infers the platform from its own operating system: the explicit
selection is part of the contract.

## Prerequisites

- The slave machine, macOS or Linux, has Python 3.9 or later available as
  `python3`.
- The ESP32-S3 native USB enumerates HID and CDC simultaneously as
  `Titella KVM`.
- The master reaches the ESP32 over UART0 (WCH bridge) or the WPA2 SoftAP, and
  holds a valid HID lease.
- The operator verified the target's active keyboard layout on the focused
  input window for this attempt and passes it explicitly with
  `layout_confirmed=true`. `slave_bootstrap` rejects a missing, unsupported or
  unconfirmed layout with `slave_layout_required`, `invalid_argument` or
  `slave_layout_unconfirmed` before any keystroke; the configured
  `keyboard_layout` is never a fallback.
- Before connecting the two cables, the VBUS safety of the rig was validated.
- The slave's graphical session is active and **unlocked**. With a locker on
  screen (for example `hyprlock`), keystrokes and bootstrap text are consumed
  at the lock screen and the loader never starts: unlock and, if applicable,
  capture the screen to confirm the state before retrying.

Native CDC paths:

| Slave system | CDC path | Permissions |
|---|---|---|
| macOS | `/dev/cu.usbmodem*` | the slave user |
| Linux | `/dev/ttyACM*` (for example `/dev/ttyACM0`) | `root:uucp`; the user must belong to the `uucp` group |

On Linux, CDC access depends on `uucp` group membership. Adding the group with
`usermod`/`gpasswd` does not affect already-open processes: a **new graphical
session must be opened** (log out and back in, or reboot) so the user inherits
the supplementary group. Without that step, the loader ends in
`cdc_not_found` even if the device enumerates correctly.

The terminal the bootstrap opens runs the user's login shell. `/bin/zsh` must
not be assumed: on a typical Linux slave the shell may be `/usr/bin/fish`. The
launch command is written for the user's real shell, not for any specific one.

If Python is missing or older than 3.9, the loader ends with
`python_unavailable` before accepting the payload. It does not download
runtimes or use Node, Java, or system packages as an alternative.

**Clipboard rigs.** With `agent_protocol = "clipboard"` the master renders the
sentinel `CLIPBOARD` where the CDC path would go, for a window rig where the
slave shares no USB with the master. No CDC is opened: the loader and the agent
carry the same v1 frames inside `TITELLA_S2M`/`TITELLA_M2S` clipboard packets.
The clipboard tool is `pbcopy`/`pbpaste` on macOS, `wl-copy`/`wl-paste` on
Wayland and `xclip` on X11; a host with none of them reports
`clipboard_unavailable` rather than silently falling back to a CDC. The agent
then starts with `--port CLIPBOARD`. Packet format, sequencing, idle silence and
acknowledgement rules are in
[the clipboard CDC section](serial-protocol.md#clipboard-cdc-agent_protocol--clipboard).

## Screen capture

`screen(source="agent")` captures the graphical session this agent runs in; it
never touches the master's UVC grabber. What the slave needs depends on the
session:

| Session | Requirement | Missing requirement |
|---|---|---|
| macOS | Screen Recording granted to the application that launches the agent | `screen_permission_denied` (not retryable). Without it `screencapture` exits 1 with "could not create image from display"; the CoreGraphics preflight turns that into a named error before the tool runs |
| X11 | ImageMagick (`import`), or `xwd` plus `convert`/`magick`/`ffmpeg` | `screen_unsupported` naming the tool to install, or `screen_unsupported` when `xwd` has no converter |
| Wayland | `gdbus` (glib2) and a portal that supports `Screenshot` | `screen_unsupported` without `gdbus`; `screen_permission_denied` when the portal denies or cancels |

On macOS the permission is granted per application, and macOS attributes it to
the **responsible** process: an agent launched from Terminal inherits
Terminal's grant, while the same code started by `launchd` — or any process
with no responsible application — cannot capture even after the setting is
enabled (measured on macOS 26: preflight `False`, `screencapture` exit 1). The
loader starts the agent as a child of the shell it opened, so granting the
terminal used for `slave_bootstrap` is what matters; re-granting does not
require a new bootstrap, but the agent must be restarted.

A Wayland compositor exposes no global framebuffer, so the portal is the only
path and it may ask the user to confirm each capture. That is a property of the
session, not a defect: the master reports the denial instead of an empty image.
The response metadata names the `backend` that produced the frame.

## Flow

1. `slave_bootstrap(owner, platform="macos"|"linux", layout=...,
   layout_confirmed=true)` loads the immutable `titella-slave.pyz` shipped
   inside the installed `titella-slave-assets` wheel, validates it against the
   manifest (size and SHA-256), and selects the HID layout the caller verified
   and passed explicitly.
2. HID opens a terminal with the configured shortcut and types a fixed
   command in chunks of up to 256 characters. On macOS (`CMD`+`SPACE`) the
   shortcut opens Spotlight and `TITELLA_LAUNCHER_TEXT` types the app name,
   followed by `ENTER`. On Linux/Hyprland the shortcut opens the terminal
   directly (`CMD` is sent as Super; `TITELLA_TERMINAL_KEY=RETURN`) and
   `TITELLA_LAUNCHER_TEXT` is left empty.
3. The typed text is **not** the loader. The master types a compact
   gzip+base64 microloader (rendered by the master, never shipped) that does
   only what its Windows counterpart does: open the CDC once, announce
   `stage.ready` over the stateless `SerialMux`, accumulate the loader as it
   arrives in flow-controlled 512-character `loader.push` chunks, acknowledge
   each chunk, and execute the decompressed loader in memory. It passes the
   already-open descriptor, resolved port and `link_id`, so the port is opened
   exactly once per bootstrap.
   The master renders the configured `slave_cdc_port` into the stub as a
   quoted literal; without an override the stub probes `/dev/cu.usbmodem*` on
   macOS and `/dev/ttyACM*` on Linux. Neither the stub nor the loader reads a
   CDC path from the environment.
4. The loader creates a `0700` directory with the exact prefix
   `titella-bootstrap-` under the user's temporary directory.
5. The loader announces `loader.ready`. The master then sends
   `bootstrap.data` chunks of 512 bytes. Each chunk is acknowledged
   end-to-end; a duplicate is acknowledged again but not applied twice, and a
   gap is neither written nor acknowledged.
6. The loader checks size and SHA-256, renames the partial file, and launches
   the zipapp with the same uid, a new session, stdio closed, and no
   persistent service.
7. The agent emits `control.hello`, whose body carries the implementation
   (`python`), the real Python `runtime`, `python`, `uid`, `pid` and
   `agent: "titella-slave"`. The master sends `control.bind` with the active
   `link_id` and does not report success until it receives `status=ready`.

The loader terminates after startup; the Terminal window may be closed. A
different `link_id` does not take over the live agent.

## Timing

| Stage | Wait |
|---|---|
| HID typing of the launcher and the encoded loader | bounded by the typing chunk size; one request never exceeds 256 characters |
| `loader.ready` after the launcher | 20 s |
| `control.hello` after the payload commit | 20 s |
| `bootstrap.begin` / `commit` / `control.bind` requests | 10 s each |
| Heartbeat | 5 s |
| Disconnect grace window | 30 s |

A larger payload does not extend these waits: the payload is the fixed zipapp
from the wheel, not a repository build.

## Footprint and permissions

The agent:

- runs as the current user, never with `sudo`;
- installs no LaunchAgent, LaunchDaemon, login item, or systemd service unit,
  extension, or package;
- opens no sockets and makes no network requests of its own;
- operates on shell and files with the user's real permissions, without
  claiming a nonexistent sandbox;
- leaves as its only own footprint during the session the Python process, the
  `.pyz`, transfer temporaries, and the bootstrap directory.

The UART/CDC channel uses plaintext by an explicit physical-trust decision.
`link_id` correlates and allows a short resumption; it does not authenticate.
A process with access to the CDC or UART can observe or inject commands with
the slave user's permissions.

## Cleanup and disconnect

With a heartbeat every 5 seconds, a disconnect preserves sessions for a
30-second grace period if the same process and `link_id` reappear. Once the
grace period expires, the agent:

1. rejects new operations;
2. sends `SIGTERM` to all process groups and, after 2 seconds, `SIGKILL` to
   the survivors;
3. cancels transfers and deletes their partial files;
4. closes the CDC;
5. deletes its bootstrap directory and payload;
6. terminates.

`slave_stop` performs the same cleanup of its own resources. The next loader
only removes directories with the exact prefix `titella-bootstrap-` and older
than 24 hours. It does not touch other files, logs, or user history.

## Observability and EDR

"Discreet" means small footprint, absence of a persistent service, and
absence of its own network traffic. It does not mean hidden. The Terminal
command, the Python process, the `.pyz`, the child processes, and their file
operations are observable by Microsoft Defender on macOS and by the EDR or
audit tools present on a Linux slave.

The payload is not obfuscated, no exclusions, services, policies, telemetry,
or history are manipulated, and no alerts are suppressed. Any alert observed
during authorized testing is preserved as evidence.

## Relevant errors

| Code | Meaning | Retry |
|---|---|---|
| `slave_layout_required` | No explicit `layout`; configuration is never a bootstrap fallback. | Yes, after verifying the focused window's input source. |
| `slave_layout_unconfirmed` | `layout` passed without a real `layout_confirmed=true` for this attempt. | Yes, after a fresh verification. |
| `python_unavailable` | Python 3.9+ is missing. | No, until the prerequisite is fixed. |
| `cdc_not_found` | The native CDC cannot be located. On Linux, check `/dev/ttyACM*` and `uucp` group membership in a new graphical session. | Yes, after checking cable, permissions, and enumeration. |
| `bootstrap_loader_timeout` | The loader did not announce `loader.ready`. Check that the slave is unlocked, that the terminal shortcut exists in the compositor, and that the HID layout is correct. | Yes, after checking screen, Terminal, and layout. |
| `size_mismatch` | The received bytes do not match the declared size. | Yes, from scratch. |
| `checksum_mismatch` | Different SHA-256. | Yes, from scratch. |
| `agent_hello_timeout` | The zipapp did not announce `control.hello`. | Yes, after cleanup. |
| `takeover_denied` | A different `link_id` while the agent is alive. | No, unless the previous agent terminates. |

## Cleanup determinism

The heartbeat thread does **not** perform cleanup: it only signals expiration.
The cleanup runs on the main thread, which also waits for it to finish before
exiting. This way the process cannot terminate mid-deletion when the
interpreter shuts down daemon threads.
