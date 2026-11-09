# Serial protocol v1

## Scope and trust model

The ESP32-S3 exposes two master ports with the same protocol: **UART0** (`Serial0`) and, if the firmware is compiled with `-DTITELLA_WIFI_PASSPHRASE`, a **TCP client over a WPA2 SoftAP**. The native USB CDC connects the ESP32-S3 to the slave machine (macOS, Linux or Windows). The firmware executes the `hid` channel received through any master port locally and forwards the other channels without transforming the line.

The UART speed is configurable and is not part of the framing: the firmware sets it with `TITELLA_UART_BAUD` (115200 by default, `Serial0.begin()`) and the master with `TITELLA_SERIAL_BAUD`. Both ends must match. `hid.health` answers `firmware`, `protocol`, `uart_baud` and `wifi`, so the master can prove what was flashed. `SerialLink` uses the platform's termios constant when it exists and, on macOS, applies non-traditional speeds with `IOSSIOSPEED` after configuring termios; on other platforms a speed without a constant is rejected and the port does not open.

The link uses plaintext UTF-8 JSON by an explicit decision of physical trust. `link_id` correlates and allows resuming a short replug; **it does not authenticate or encrypt**. Any process with access to the CDC, the UART or the SoftAP network can observe or inject operations with the permissions of the slave user.

## Master transports

| Transport | Activation | Direction and rules |
|---|---|---|
| UART0 | Always | Master port; framing and rules identical to TCP. |
| TCP over SoftAP | Compile with `-DTITELLA_WIFI_PASSPHRASE="..."` | Master port; same framing and same direction rules. |

The SoftAP is a private network created by the ESP32-S3: SSID `TITELLA_WIFI_SSID` (`Titella` by default), WPA2 with `TITELLA_WIFI_PASSPHRASE` and address `192.168.4.1`. The server listens on `TITELLA_WIFI_TCP_PORT` (5555 by default). The master associates to that network and connects with `TITELLA_TRANSPORT=wifi`, `TITELLA_WIFI_HOST` and `TITELLA_WIFI_PORT`.

- **Single TCP client.** A new connection replaces the previous one: the ESP32 cuts the previous client, discards whatever remained to be sent and accepts the new one. This is safer than refusing, because a dead client cannot block the only Wi-Fi port forever.
- **Transport contract.** Both ports go through the same state machine: hid is executed locally and the rest is forwarded to the CDC. Direction rules and frame limits do not change.
- **Response routing.** Frames from the slave return to the master port whose `link_id` matches. If the master switches transports while keeping its `link_id`, the port with the most recent activity wins. If no port has claimed that `link_id` yet, the frame is delivered to all connected masters and each discards the ones not its own.
- **Security.** The WPA2 passphrase is the network's access control; TCP adds no encryption or authentication of its own. `link_id` remains correlation, not a credential. Without the macro, or with a passphrase shorter than 8 characters, Wi-Fi stays disabled and behavior is identical to the UART-only version (`wifi.enabled=false`).
- **Non-blocking.** Accepting connections and sending use `MSG_DONTWAIT`; what the socket does not accept accumulates in a bounded buffer that is drained on each router pass. A client that stops reading gets disconnected instead of holding back the HID `loop()`.
- **Autodetection and modem lines.** They only exist on the serial transport. With `TITELLA_TRANSPORT=wifi` no `/dev/cu.*` is probed, termios is not applied and DTR/RTS are not released; on TCP the equivalent is the `connect` itself with backoff.

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
| `channel` | string | `hid`, `control`, `bootstrap`, `shell`, `fs` or `transfer`. |
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
| Master (UART0 or TCP) | `control` | `heartbeat`, `request`, `ack` | Slave via CDC. |
| Master (UART0 or TCP) | `bootstrap` | `request`, `data`, `ack` | Loader via CDC. |
| Master (UART0 or TCP) | `shell`, `fs`, `transfer` | `request`, `data`, `ack` | Agent via CDC. |
| Slave | `control` | `hello`, `heartbeat`, `response`, `ack`, `event`, `error` | Master with matching `link_id`. |
| Slave | `bootstrap` | `response`, `ack`, `event`, `error` | Master with matching `link_id`. |
| Slave | `shell`, `fs`, `transfer` | `response`, `data`, `ack`, `event`, `error` | Master with matching `link_id`. |

The firmware rejects `channel=hid` received from the CDC and never calls the HID controller from that side. Unknown versions, channels, types or shapes produce an `error` on the originating port and are not forwarded.

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
