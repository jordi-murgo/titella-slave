# Serial protocol v1

## Scope and trust model

The ESP32-S3 exposes two master ports with the same protocol: **UART0** (`Serial0`) and, if the firmware is compiled with `-DTITELLA_WIFI_PASSPHRASE`, a **TCP client over a WPA2 SoftAP**. The native USB CDC connects the ESP32-S3 to the slave machine (macOS, Linux or Windows). The firmware executes the `hid` and `video` channels received through any master port locally and forwards the other channels without transforming the line. `video` stages the static frame of the emulated UVC camera described in the firmware README.

The UART speed is fixed by the build rather than discovered: the firmware sets it with `TITELLA_UART_BAUD` (2,000,000 baud by default and in `build_opt.h`, `Serial0.begin()`) and the master with `TITELLA_SERIAL_BAUD`, which also defaults to 2,000,000. Both ends must match; 115200 was the historical default and a mismatch garbles every frame. `hid.health` answers `firmware`, `protocol`, `uart_baud`, `wifi`, `slave_transports`, the active `slave_transport`, `hid_data_protocol`, `usb_vid`, `usb_pid`, `usb_serial` and `slave_connected`, so the master can prove what was flashed, whether the HID data transport exists, and whether the destination it forwards to right now still has a stream attached. `slave_connected` is read-only state of the selected destination: `HidPort.connected()` for the HID data endpoint, or `CdcPort.connected()` for the CDC. The core's `USBCDC::operator bool` returns its `connected` flag, which `_onLineState` sets when the host asserts `DTR && RTS` and clears when `DTR` drops — so the CDC value tracks the host's open line, not mere enumeration. The stop path proves a closed stream with it instead of inferring one from a failed RPC. `SerialLink` uses the platform's termios constant when it exists and, on macOS, applies non-traditional speeds with `IOSSIOSPEED` after configuring termios; on other platforms a speed without a constant is rejected and the port does not open.

## Slave data transport (`slave_transport = "cdc" | "hid"`)

The master→ESP32 link and the ESP32→slave leg are separate selections. `slave_transport` names the second one:

- `cdc` (default) forwards the protocol v1 frames to the native USB CDC the slave opens; this is the path every earlier firmware carries.
- `hid` forwards the same frames to the vendor-defined HID data collection (reports `0x10`/`0x11`). It needs `transport=serial`, `agent_protocol=serial`, `slave_platform=windows` and no `slave_cdc_port`, and only firmware 1.6.0 or newer advertises it (`hid_data_protocol: 1`). The bootstrap stages the compressed loader in the Feature `0x11` mailbox over the UART and types only a bounded stage-0 line, so the target uses no COM port, no clipboard and no network. There is no automatic fallback: an old firmware answers `slave_transport_unsupported` before any keystroke.

The selection is a local `hid.slave_transport` operation of the firmware (`{"transport":"cdc"|"hid"}`); it is refused with `slave_transport_busy` while frames are still queued, and only the selected destination is pumped. A new HID epoch, a close and a physical disconnect clear the selected destination's parser and queue so no byte of a previous session joins the next one.

The link uses plaintext UTF-8 JSON by an explicit decision of physical trust. `link_id` correlates and allows resuming a short replug; **it does not authenticate or encrypt**. Any process with access to the CDC, the UART or the SoftAP network can observe or inject operations with the permissions of the slave user.

## Master transports

| Transport | Activation | Direction and rules |
|---|---|---|
| UART0 | Always | Master port; framing and rules identical to TCP. |
| TCP over SoftAP | Compile with `-DTITELLA_WIFI_PASSPHRASE="..."` | Master port; same framing and same direction rules. |

The SoftAP is a private network created by the ESP32-S3: SSID `TITELLA_WIFI_SSID` (`Titella` by default), WPA2 with `TITELLA_WIFI_PASSPHRASE` and address `192.168.4.1`. The server listens on `TITELLA_WIFI_TCP_PORT` (5555 by default). The master associates to that network and connects with `TITELLA_TRANSPORT=wifi`, `TITELLA_WIFI_HOST` and `TITELLA_WIFI_PORT`.

- **Single TCP client.** A new connection replaces the previous one: the ESP32 cuts the previous client, discards whatever remained to be sent and accepts the new one. This is safer than refusing, because a dead client cannot block the only Wi-Fi port forever.
- **Transport contract.** Both ports go through the same state machine: hid and video are executed locally and the rest is forwarded to the CDC. Direction rules and frame limits do not change.
- **Response routing.** Frames from the slave return to the master port whose `link_id` matches. If the master switches transports while keeping its `link_id`, the port with the most recent activity wins. If no port has claimed that `link_id` yet, the frame is delivered to all connected masters and each discards the ones not its own.
- **Security.** The WPA2 passphrase is the network's access control; TCP adds no encryption or authentication of its own. `link_id` remains correlation, not a credential. Without the macro, or with a passphrase shorter than 8 characters, Wi-Fi stays disabled and behavior is identical to the UART-only version (`wifi.enabled=false`).
- **Non-blocking.** Accepting connections and sending use `MSG_DONTWAIT`; what the socket does not accept accumulates in a bounded buffer that is drained on each router pass. A client that stops reading gets disconnected instead of holding back the HID `loop()`.
- **Autodetection and modem lines.** They only exist on the serial transport. With `TITELLA_TRANSPORT=wifi` no `/dev/cu.*` is probed, termios is not applied and DTR/RTS are not released; on TCP the equivalent is the `connect` itself with backoff.

## Clipboard CDC (`agent_protocol = "clipboard"`)

A window rig has no CDC port of its own: the agent runs inside a remote desktop or
an app window, so the master's clipboard is the only shared channel. Selecting
`agent_protocol = "clipboard"` renders the sentinel `CLIPBOARD` where a port name
would go; the loader and the agent then carry the same v1 frames inside clipboard
packets instead of a serial byte stream.

```
master to slave:  TITELLA_M2S:<link_id>:<pkt_seq>:<ack_s2m_seq>\n<frame>\n<frame>...
slave to master:  TITELLA_S2M:<link_id>:<pkt_seq>:<ack_m2s_seq>\n<frame>\n<frame>...
```

- `pkt_seq` starts at 1 and increases by one for every packet that carries at least
  one frame. `ack_*` is the highest `pkt_seq` this side has consumed from the peer
  (`0` before the first packet).
- **Packets, not streams.** One packet carries up to 16 frame lines. A packet with
  zero lines and `pkt_seq = 0` is a pure acknowledgement: it never needs an
  acknowledgement of its own, which is what keeps the exchange from ping-ponging.
- **Stop and wait.** A sender keeps at most one unacknowledged packet and re-asserts
  it when another writer overwrites the clipboard before the peer reads it. After a
  bounded number of re-assertions the packet is abandoned so the block-level ACK
  logic above retries it; the user clipboard is never held hostage.
- **Idle silence.** A quiet link writes nothing: the agent feeds its own grace clock a
  synthetic local `control.heartbeat` every 2 s and drops the idle heartbeats it
  would otherwise send, so neither side polls or rewrites the clipboard while idle.
  `control.agent.stop` still stops the agent immediately.
- **Clipboard history.** Packets written on Windows also carry the
  `ExcludeClipboardContentFromMonitorProcessing` and
  `CanIncludeInClipboardHistory` formats so clipboard managers and sync monitors
  ignore them. On the master side the packet types
  `org.nspasteboard.TransientType`, `org.nspasteboard.AutoGeneratedType` and
  `org.nspasteboard.ConcealedType` play the same role.
- **Tools.** The POSIX agent uses `pbcopy`/`pbpaste` on macOS, `wl-copy`/`wl-paste`
  on Wayland and `xclip` on X11; a host with none of them reports
  `clipboard_unavailable` instead of falling back to another channel.
- **Line endings.** A clipboard sync layer may normalize LF to CRLF; each side trims
  a trailing CR per line, so framing survives the hop unchanged.

## Framing and limits

- A frame is a UTF-8 JSON object followed by LF (`0x0a`). CRLF is accepted.
- The JSON, without LF, occupies at most 4096 bytes.
- Each receiver keeps at most 4097 bytes. If the limit is exceeded, it discards bytes up to the next LF before accepting frames again.
- Boot banners and lines that are not valid v1 JSON are not interpreted as responses.
- Binary data is Base64-encoded. Each `data` contains at most 512 decoded bytes.
- Master limits: 256 concurrent requests, 1024 frames in the outbound queue and 1024 deduplication keys.

## Envelope

All frames include:

| Field | Type | Meaning |
|---|---|---|
| `v` | integer | Version; in this cut always `1`. |
| `channel` | string | `hid`, `video`, `control`, `bootstrap`, `shell`, `fs` or `transfer`. |
| `type` | string | `hello`, `heartbeat`, `request`, `response`, `data`, `ack`, `event` or `error`. |
| `link_id` | string | 128 random bits represented with 32 hexadecimal characters. It is not a secret or a credential. |

Fields depending on the type:

- `request` and `response`: monotonic integer `request_id`, `op` and a small `body`.
- `data`: `session_id`, `seq` and `data_b64`.
- `ack`: `session_id` and `ack_seq`.
- `event` and `error`: `op` and/or `body` where applicable.

Example HID master → firmware:

```json
{"v":1,"channel":"hid","type":"request","link_id":"0123456789abcdef0123456789abcdef","request_id":7,"op":"mouse_rel","body":{"x":4,"y":-2}}
```

Response firmware → master:

```json
{"v":1,"channel":"hid","type":"response","link_id":"0123456789abcdef0123456789abcdef","request_id":7,"op":"mouse_rel","body":{"ok":true}}
```

Terminal chunk slave → master:

```json
{"v":1,"channel":"shell","type":"data","link_id":"0123456789abcdef0123456789abcdef","session_id":"9af0","seq":12,"data_b64":"b2sK"}
```

## Allowed directions

| Origin | Channel | Allowed types | Destination |
|---|---|---|---|
| Master (UART0 or TCP) | `hid` | `request` | Firmware; never forwarded to the CDC. |
| Firmware | `hid` | `response`, `error` | Originating master port. |
| Master (UART0 or TCP) | `video` | `request` | Firmware; never forwarded to the CDC. |
| Firmware | `video` | `response`, `error` | Originating master port. |
| Master (UART0 or TCP) | `control` | `heartbeat`, `request`, `ack` | Slave via CDC. |
| Master (UART0 or TCP) | `bootstrap` | `request`, `data`, `ack` | Loader via CDC. |
| Master (UART0 or TCP) | `shell`, `fs`, `transfer` | `request`, `data`, `ack` | Agent via CDC. |
| Slave | `control` | `hello`, `heartbeat`, `response`, `ack`, `event`, `error` | Master with matching `link_id`. |
| Slave | `bootstrap` | `response`, `ack`, `event`, `error` | Master with matching `link_id`. |
| Slave | `shell`, `fs`, `transfer` | `response`, `data`, `ack`, `event`, `error` | Master with matching `link_id`. |

The firmware rejects `channel=hid` received from the CDC and never calls the HID controller from that side. It rejects `channel=video` from the CDC for the same reason: the camera image comes from the master, so a slave frame must not be able to overwrite the staged picture. Unknown versions, channels, types or shapes produce an `error` on the originating port and are not forwarded.

## Correlation, ACK and deduplication

`request_id` correlates each small request with exactly one `response` or `error`. No receiver associates "the first response with `ok`" to an operation.

The interactive channels (`shell`, `bootstrap` and the `fs` streams) keep a single pending `data` frame per direction: the sender waits for the end-to-end ACK before sending the next block.

File transfers (`transfer`) use a sliding window with per-block ACK:

1. The sender keeps up to 8 blocks of 512 bytes unacknowledged. It only reads the next block when there is a free slot, so the read-ahead is bounded by the window.
2. Each block keeps its own ACK: an `ack_seq=seq` frees its slot as soon as it arrives, without waiting for the rest of the window, and ACKs may arrive out of order.
3. If a block is not acknowledged within `ack_timeout_s`, expired blocks are resent in sequence order; after `ack_retries` resends `ack_timeout` is reported and the transfer fails in a bounded way.
4. `upload.commit` and `transfer.finished` are only emitted when all in-flight blocks have their ACK.

The receiver accepts only `seq == expected`: a gap is neither written nor acknowledged (the sender will resend it) and a block earlier than the expected one is acknowledged again without being rewritten. The deduplication key `(link_id, session_id, seq)` is registered only after accepting the frame; registering earlier would turn the resend of a rejected gap into a false ACK. An already-applied duplicate is acknowledged again, but its bytes are not applied again.

The upload window is set by the master; the download window travels in the body of `download.begin` (`window`, 1–64, 8 by default) so that window 1 and 8 can be compared with the same binary.

## Screen capture: `fs.screen`

`fs.screen` is the only file operation that returns image bytes. Both slave implementations serve it: the Windows agent captures its own desktop, and the Python agent captures the graphical session it runs in. A backend that is unavailable — no Windows agent, no `screencapture`, no X11 tool, no portal — answers its own error code and the master never falls back to another capture path. It is an extension of the existing `fs` channel, not a protocol version change.

Backends and what each one guarantees:

| Backend | Selection | Failure that is reported, never faked |
|---|---|---|
| `windows-dotnet` | the Windows agent | `screen_capture_failed` with the real diagnostic |
| `macos-screencapture` | macOS session | `screen_permission_denied` when Screen Recording is not granted: without it `screencapture` exits 1 with "could not create image from display", and the CoreGraphics preflight names the cause before the tool runs |
| `x11-import` | `XDG_SESSION_TYPE=x11` and ImageMagick present | `screen_unsupported` naming the missing tool |
| `x11-ffmpeg` | X11 with `ffmpeg` when `import` is absent | `screen_capture_failed` with ffmpeg's diagnostic |
| `wayland-portal` | `XDG_SESSION_TYPE=wayland` | `screen_permission_denied` when the portal denies or cancels: a Wayland compositor has no global framebuffer, so the user is part of the capture |

The `backend` field in the response metadata names the one that produced the frame. It is diagnostic only.

Request:

```json
{"channel":"fs","type":"request","link_id":"<link>","request_id":42,"op":"screen","body":{"stream_id":"<generated id>"}}
```

Response metadata, followed by the ordinary `data` frames of the stream and the `fs.stream.finished` event:

```json
{"format":"png","width_px":2560,"height_px":1440,"bytes":214533,"source":"agent","monitor":"primary","stream_id":"<generated id>","payload_kind":"bytes"}
```

- The payload is one PNG of the agent's **primary monitor** at native resolution, origin `(0,0)`, with no base64 image inside the metadata.
- The `backend` field is only present on implementations that have more than one capture path (the Python agent); a receiver must not require it.
- Frames carry monotonically increasing `seq` and at most 512 decoded bytes each. `fs.screen` uses the **windowed** sender of the transfer channel (per-block ACK, bounded resends, several blocks in flight), not the stop-and-wait used by the other `fs` streams: measured on the rig, stop-and-wait sustains about 3.5 KiB/s against 30–45 KiB/s windowed, and a full-screen PNG is far too large for the former. The master therefore reassembles by `seq` and acknowledges every block, holding out-of-order ones until the hole is filled; the hold is bounded by the payload ceiling.
- The final event reports `status:"finished"` and the transmitted byte count, which must equal `bytes` and the length of the PNG actually received. A shorter payload is a truncated transfer: the master rejects it as a retryable `screen_capture_failed` and publishes no image. Observed on the rig with a ~1.7 MiB full-screen PNG: losses of roughly one to two blocks recurred at every window size (2, 4 and 8), while ~1.2 MiB captures completed byte-exact. The loss is in the UART/ESP32 hop, so the master treats it as retryable and never publishes a partial image.
- The master compares the declared dimensions with the PNG IHDR: an inconsistent response is rejected and no image is published.
- The capture is best effort: a locked session, secure desktop, protected surface or disconnected RDP session may be reported as `screen_capture_failed` (retryable) with the real diagnostic. The agent encodes the PNG in memory and writes no screenshot file.
- `screen_too_large` (not retryable) appears when the encoded PNG exceeds the 32 MiB ceiling. That ceiling is a memory guard for the agent and the master's receiver bound; it does not promise that an image below it crosses the link within the master's deadline.

A receiver that abandons the stream stops acknowledging blocks, so the slave stops after its bounded ACK retries (`ack_timeout`, `1 + 3` resends of one second each). There is no second cancellation protocol.

## Priority

The master queue is stable within each level:

1. ACK, heartbeat and HID.
2. Shell control and small requests.
3. Shell data.
4. Transfers.

A started frame is not interrupted. The 512-byte maximum chunk bounds the HID wait during streaming.

## Heartbeat and reconnection

Master and agent send a heartbeat every 5 seconds by default. Both record `last_seen`. An `EIO` error, EOF, a socket cut or the disappearance of the device closes the descriptor and reactivates reconnection with a backoff of 0.5, 1, 2 and 5 seconds. On serial, reconnection autodetects the port again; on TCP it repeats the `connect` to the SoftAP. In both cases the `link_id` is preserved and, with it, the grace window.

For 30 seconds, sessions and transfers remain in `reconnecting` state. They only continue if the same process and `link_id` reappear. After the grace period, the master marks the work as disconnected and the agent kills its process groups, cancels transfers, removes its temporaries and exits.

The master temporarily stores the `link_id` with mode `0600` inside `TITELLA_STATE_DIR` for a short replug. It removes it on a clean shutdown. This file is not a credential either.

## Compatibility

There is no compatibility with the previous protocol of top-level objects `{"op": ...}` nor with responses without `request_id`. Firmware, probe, master, loader and agent must use v1 together.

## Agent state and attachment

The master keeps an availability gate per MCP server process. `remote_shell`, `remote_terminal_open` and all `fs`/transfer operations require the agent to be ready; if it is not, they answer immediately with `agent_not_ready` instead of waiting for the transport timeout.

The gate opens through two paths:

1. `slave_bootstrap` completes: `agent.hello` → `control.bind` → `control.ready`.
2. A spontaneous `control.hello` (process already alive after a replug): the master sends `control.bind` and opens the gate upon receiving `ready`.

The gate closes on `slave_stop`, on a failed bootstrap and when the lease cannot be verified.
