# LiteRemote Wire Protocol — Specification v1

> **Status:** Draft-freeze of the protocol as implemented in `RemoteDesktop.Shared` (net8.0), captured
> during audit **M-A0** (2026-07-11). This document is the **contract every client must implement** —
> the Windows viewer, the upcoming Android/Mac (MAUI) viewer, and any third party. Once ratified it is
> frozen as **protocol v1**; breaking changes require a version bump (see §3).
>
> This file MAY be public (it documents an interoperable protocol, no secrets).
> Byte offsets and enum values below are normative and taken directly from the source.

---

## 1. Layers

```
┌──────────────────────────────────────────────┐
│ Application messages (this spec, §5–§10)      │  MessageType + payload
├──────────────────────────────────────────────┤
│ Framing (§4): [type:1][len:4 LE][payload:len] │
├──────────────────────────────────────────────┤
│ TLS 1.2/1.3 (§2): self-signed + TOFU pinning  │
├──────────────────────────────────────────────┤
│ TCP  (host listens on 7443; or relay-spliced) │
└──────────────────────────────────────────────┘
```

Two ways a viewer reaches a host:
- **Direct:** TCP to `host:7443`, TLS handshake, then the message protocol.
- **Relay (ID mode):** both sides connect to a relay (§11); after the splice the byte pipe carries the
  *same* TLS session end-to-end, so the relay never sees plaintext and pinning still defeats a hostile relay.

All multi-byte integers are **little-endian**. Strings are UTF-8, length-prefixed (§5).

## 2. Transport security (TLS + trust-on-first-use)

- The host owns a **self-signed ECDSA P-256** certificate (`CN=RemoteDesktopHost`, validity ~5 years,
  EKU `serverAuth`), persisted at `%LocalAppData%\LiteRemote\host.pfx`.
- There is **no public CA**. The client pins the host identity **trust-on-first-use**:
  - **Pin value** = `SHA-256(SubjectPublicKeyInfo)`, uppercase hex (display form colon-separated).
  - First connect → fingerprint shown to user for out-of-band confirmation, then stored in
    `%LocalAppData%\LiteRemote\pins.json` keyed by `"host:port"`.
  - Later connect → **Match** proceeds; **Mismatch** must warn ("certificate changed — possible MITM")
    and only re-pin on explicit user confirmation; **FirstUse** prompts.
- **Mobile clients MUST implement the same pin model** (compute SHA-256 of the server SPKI, keep a
  per-endpoint pin store, prompt on first use / mismatch). Do **not** fall back to system CA trust.

## 3. Versioning & capability negotiation

**Implemented (M-A1, AUD-010).** The wire-protocol version is carried **inside the existing auth
handshake**, not a separate exchange — this adds no round-trip and stays interoperable with peers that
predate versioning:

- `AuthRequest` carries `protocolVersion` (the host's version); `AuthResponse` carries `protocolVersion`
  (the client's version). Both are JSON fields (§7).
- A peer that predates versioning simply omits the field; it deserializes to **1** (see
  `ProtocolInfo.Current`), so the current Windows host and client interoperate with a versioned mobile
  client **without any change**.
- The host rejects a client whose `protocolVersion < ProtocolInfo.MinSupported` with
  `AuthResult{ ok=false, reason="Incompatible client protocol vX" }` and closes — it never proceeds
  into frames it cannot parse.

Today `ProtocolInfo.Current = 1` and `MinSupported = 1`, so every current peer is accepted; the
mechanism exists so a future breaking change can be gated cleanly.

**Compatibility rule:** never change the meaning or size of an existing payload field; add new data
only behind a bumped `Current` (and raise `MinSupported` only when a change is genuinely breaking).
`MessageType.Hello`/`HelloAck` remain **reserved** for a future, richer pre-auth capability handshake
(e.g. negotiating codec/feature flags before auth); they are unused in v1.

## 4. Framing

Every message is a fixed 5-byte header followed by the payload:

```
offset 0: u8   MessageType
offset 1: u32  payloadLength   (little-endian, unsigned)
offset 5: …    payload (payloadLength bytes)
```

- **`MaxPayloadSize` = 64 MiB.** On read, a length that is negative-as-int or `> MaxPayloadSize` is a
  protocol violation → close the connection. (The `u32` is cast to `int`; values `> 2^31` read as
  negative and are rejected by the same bound.)
- The transport is **full-duplex** with two independent send lanes (latency-critical design):
  - **Control lane** — unbounded, lossless, always flushed first: auth, settings, input, clipboard,
    keyframe requests, ping/pong, stat. A keystroke is never stuck behind video.
  - **Video lane** — a shallow bounded queue (depth **2**) with *blocking* backpressure. Video frames
    are dirty-tile **deltas** and must **not** be dropped (a lost delta leaves stale pixels); instead
    the sender blocks, throttling the encoder to link speed and capping latency at ~2 frames.
- Each message is flushed immediately (latency over throughput).

## 5. Primitive encoding (control payloads)

Hot paths (input, video, clipboard) use hand-rolled little-endian codecs; rare handshake payloads
(auth) use JSON (§7). The primitive cursor used by control payloads:

| Type   | Wire                                             |
|--------|--------------------------------------------------|
| `U8`   | 1 byte                                           |
| `Bool` | 1 byte (0/1)                                      |
| `I16`  | 2 bytes LE, signed                               |
| `U16`  | 2 bytes LE, unsigned                             |
| `I32`  | 4 bytes LE, signed                               |
| `I64`  | 8 bytes LE, signed (used for `double` bit-pattern)|
| `Str`  | `U16` byte-length **then** that many UTF-8 bytes |

## 6. Message registry

| Value | Name             | Dir      | Payload (§)          |
|-------|------------------|----------|----------------------|
| 1     | Hello            | C→H      | §3 (reserved/v1)     |
| 2     | HelloAck         | H→C      | §3 (reserved/v1)     |
| 3     | AuthRequest      | H→C      | §7 JSON              |
| 4     | AuthResponse     | C→H      | §7 JSON              |
| 5     | AuthResult       | H→C      | §7 JSON              |
| 10    | VideoConfig      | H→C      | §8.1                 |
| 11    | VideoFrame       | H→C      | §9                   |
| 12    | KeyFrameRequest  | C→H      | empty                |
| 20    | MouseMove        | C→H      | §8.2                 |
| 21    | MouseButton      | C→H      | §8.2                 |
| 22    | MouseWheel       | C→H      | §8.2                 |
| 23    | KeyEvent         | C→H      | §8.2                 |
| 30    | ClipboardUpdate  | C↔H      | §10                  |
| 31    | ClipboardRequest | C↔H      | (pull large payload) |
| 40    | SettingsUpdate   | C→H      | §8.3                 |
| 41    | DisplayList      | H→C      | §8.4                 |
| 42    | Stat             | H→C      | §8.5                 |
| 90    | Ping             | C↔H      | empty                |
| 91    | Pong             | C↔H      | empty                |
| 99    | Bye              | C↔H      | empty                |

## 7. Handshake & authentication

**Actual sequence** (after TLS; Hello/HelloAck currently skipped — see §3):

```
Host  → Client : AuthRequest { methods, nonce }
Client → Host  : AuthResponse { method, secret }
Host  → Client : AuthResult  { ok, reason, sessionToken }
   (on ok) Client → Host : SettingsUpdate  → host starts capture → VideoConfig, VideoFrame…
```

Payloads are **JSON (camelCase)** inside TLS:

- `AuthRequest`  = `{ "methods": <u8 bitmask>, "nonce": "<32 hex chars>", "protocolVersion": <int> }`
  `AuthMethod`: `None=0, Password=1, Google=2` (bit-flags; host advertises what it accepts).
- `AuthResponse` = `{ "method": <u8>, "secret": "<password | google id_token>", "protocolVersion": <int> }`
  `protocolVersion` (both messages) is optional on the wire; absent ⇒ v1 (§3).
- `AuthResult`   = `{ "ok": <bool>, "reason": "<text>", "sessionToken": "<hex | empty>" }`

Notes / normative behavior:
- The **password is sent as cleartext inside TLS**; the host verifies it against a stored Argon2id
  hash (§7.1). This is standard server-side verification and safe **only** because of TLS + pinning.
- The `nonce` is currently **not incorporated into the client's proof** and is not verified by the
  host — it provides no replay protection today. Either bind it into the proof or drop the pretense
  (tracked as an audit finding).
- Host enforces a **30-second** auth window, then closes.
- For Google, the host additionally checks `email_verified == true` **and** an allow-list of emails.

### 7.1 Password hashing (Argon2id)

Stored (host config) as one string:
```
argon2id$v=19$m=65536,t=3,p=2$<base64 salt(16B)>$<base64 hash(32B)>
```
- Params: memory **64 MiB**, iterations **3**, parallelism **2**, salt 16 B, hash 32 B.
- Verification re-derives with the params **read from the stored string** and compares with a
  constant-time equality. (Implementations MUST reject a stored string that requests absurd params.)

### 7.2 Google OIDC (`id_token`) verification

Offline verification against Google JWKS (`https://www.googleapis.com/oauth2/v3/certs`, cached 6 h):
- Header `alg` MUST be `RS256` and carry a `kid`; signature verified over `header.payload` ASCII.
- `iss ∈ {accounts.google.com, https://accounts.google.com}`, `aud == <configured client id>`,
  `exp` in the future. (`nbf`/`iat` and clock-skew handling are recommended additions.)
- The **host** must require `email_verified` and match the email against its allow-list.

## 8. Control payloads

### 8.1 VideoConfig (13 bytes) — H→C, sent before the first frame
`I32 Width · I32 Height · U8 Codec · I32 TileSize`
`VideoCodec`: `JpegTiles=0, H264=1, H265=2`.

### 8.2 Input events — C→H (coordinates are normalized 0..65535, resolution-independent)
| Msg         | Bytes | Fields |
|-------------|-------|--------|
| MouseMove   | 4     | `U16 Nx · U16 Ny` |
| MouseButton | 6     | `U8 Button · Bool Down · U16 Nx · U16 Ny` |
| MouseWheel  | 8     | `I16 DeltaX · I16 DeltaY · U16 Nx · U16 Ny` |
| KeyEvent    | 6     | `U16 VirtualKey · U16 ScanCode · Bool Down · Bool Extended` |

`MouseButton`: `Left=0, Right=1, Middle=2, X1=3, X2=4`.
`KeyEvent` carries the **Windows virtual-key** + scancode; non-Windows clients must map their key
events to VK codes (a mapping table is a mobile-client deliverable).

### 8.3 SettingsUpdate (27 bytes) — C→H
`U8 FrameRateMode · I32 TargetFps · I32 MaxFps · U8 ResolutionMode · I32 ScaledWidth ·
 I32 ScaledHeight · I32 DisplayIndex · U8 PreferredCodec · U8 Quality · Bool ClipboardSync ·
 Bool BlankHostScreen · Bool LockHostInput`
`FrameRateMode`: `Auto=0, Fixed=1`. `ResolutionMode`: `Native=0, Scaled=1, MatchClient=2`.
`Quality`: 1..100. First `SettingsUpdate` triggers the host capture thread.

### 8.4 DisplayList (variable) — H→C
`U16 count`, then per display: `I32 Index · Str DeviceName · I32 X · I32 Y · I32 Width · I32 Height ·
Bool IsPrimary · I32 RefreshHz`.

### 8.5 Stat (variable) — H→C (telemetry)
`I32 Fps · I64 MbitsPerSecond(double bits) · I32 RoundTripMs · I32 EncodeMs · Str EncoderName`.

## 9. VideoFrame codec — H→C

```
[u32 frameId][u8 flags][u16 tileCount]
repeat tileCount times:
  [u16 x][u16 y][u16 w][u16 h][u32 dataLen][dataLen bytes]
```
- `FrameFlags`: `None=0, KeyFrame=1` (full frame — safe to start decoding), `Continued=2` (reserved).
- Tile `Data` is codec-specific:
  - **`JpegTiles`** — each tile is an independent JPEG for the `[x,y,w,h]` dirty rectangle; a frame may
    carry many tiles. Blit each onto the surface at its offset.
  - **`H264` / `H265`** — the frame carries **exactly one tile** whose `Data` is a **complete Annex-B
    access unit** (NAL units separated by `00 00 00 01` start codes). A `KeyFrame` access unit is
    SPS + PPS + IDR (a decoder may start here); a non-keyframe is a single P-frame. `[x,y,w,h]` cover
    the full frame. The hardware decoder pads height/width up to a macroblock multiple (e.g. 1080→1088);
    the client **must crop** the decoded picture to the `VideoConfig` `Width×Height` (top-left region) —
    otherwise the padded picture mismatches the surface and renders black. Feed the raw `Data` bytes
    straight to the platform decoder (Media Foundation on Windows, MediaCodec `video/avc` on Android,
    VideoToolbox on Mac) — SPS/PPS are inline in the keyframe, so no out-of-band codec config is needed.
- `KeyFrameRequest` (empty payload) asks the host to resend a full frame (e.g. after packet loss or a
  fresh viewer join). Decoders must not start on a non-keyframe.

## 10. Clipboard — C↔H

```
[u8 format][i32 length][length bytes]
```
`ClipboardFormat`: `Empty=0, Text=1 (UTF-8), Png=2 (image bytes), FileList=3 (\n-separated paths)`.
Echo suppression uses an FNV-1a fingerprint over `format+bytes` so a pushed value doesn't loop back.
`ClipboardSync` in settings gates the whole feature; large payloads may be pulled with
`ClipboardRequest`.

## 11. Relay (ID rendezvous)

Newline-delimited JSON handshake on the relay's control connection (default port **7500**), then the
socket becomes a raw pipe carrying the end-to-end TLS session.

`RelayMsg = { op, id, key, session, ok, error }` — one compact JSON object per line, `\n`-terminated,
**max line 4096 bytes**.
- `op`: `register | offer | join | connect | ping | pong`.
- `id`: 9-digit host id (display form `123 456 789`). `key`: host secret that defends the id against
  takeover. `session`: one-shot splice token.
- Clients normalize input ids by stripping non-digits; the relay MUST validate an id is exactly 9
  digits and authenticate `key` before (re)binding an id.

## 12. Validation rules — REQUIRED for every implementation

The framing layer bounds the *frame* size (§4), but the individual payload deserializers must not
trust their input. **Every implementation (especially the new mobile decoders) MUST:**

1. Before each field read, check enough bytes remain; a short/misdeclared buffer is a **protocol
   violation** → stop parsing and close politely (do not throw into the read loop).
2. For `VideoFrame`: validate `tileCount` and, for every tile, `0 ≤ dataLen` **and**
   `pos + dataLen ≤ payloadLength`; reject the frame otherwise (never `Slice` out of range).
3. For `Str`: validate the `U16` length against remaining bytes before slicing.
4. For `Clipboard`: treat `length` as unsigned intent; reject `length < 0` or
   `length > payloadLength − 5`; enforce a sane clipboard cap.
5. Reject unknown/invalid enum values (`MessageType`, `AuthMethod`, `ClipboardFormat`, `VideoCodec`,
   `MouseButton`, `FrameRateMode`, `ResolutionMode`) instead of coercing them.
6. Treat a parse failure as an attacker/corruption signal — close the session, don't crash the app.

These rules are the reference behavior; the M-A0 audit report (`docs/AUDIT-PLAN.md` workflow) tracks
the concrete code fixes that make `RemoteDesktop.Shared` match this section.

## 13. Enum reference (normative values)

```
MessageType   : Hello=1 HelloAck=2 AuthRequest=3 AuthResponse=4 AuthResult=5
                VideoConfig=10 VideoFrame=11 KeyFrameRequest=12
                MouseMove=20 MouseButton=21 MouseWheel=22 KeyEvent=23
                ClipboardUpdate=30 ClipboardRequest=31
                SettingsUpdate=40 DisplayList=41 Stat=42  Ping=90 Pong=91 Bye=99
AuthMethod    : None=0 Password=1 Google=2            (bit-flags)
VideoCodec    : JpegTiles=0 H264=1 H265=2
FrameFlags    : None=0 KeyFrame=1 Continued=2         (bit-flags)
ClipboardFormat: Empty=0 Text=1 Png=2 FileList=3
MouseButton   : Left=0 Right=1 Middle=2 X1=3 X2=4
FrameRateMode : Auto=0 Fixed=1
ResolutionMode: Native=0 Scaled=1 MatchClient=2
```

---
*Generated during audit M-A0. Keep this file in lock-step with `RemoteDesktop.Shared`; any wire change
is a protocol-version event (§3).*
