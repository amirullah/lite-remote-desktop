# LiteRemote

**A fast, lightweight, and secure remote-desktop app for Windows.**

[![Build & Package](https://github.com/amirullah/lite-remote-desktop/actions/workflows/build.yml/badge.svg)](https://github.com/amirullah/lite-remote-desktop/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/amirullah/lite-remote-desktop?sort=semver)](https://github.com/amirullah/lite-remote-desktop/releases/latest)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](#)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

LiteRemote lets you view and control another Windows PC over your LAN or the internet. It's built to
feel instant: the screen is captured on the GPU (Desktop Duplication, with an automatic GDI fallback),
encoded with hardware **H.264** where available, and streamed over a pinned **TLS** connection. When you
prefer the tried-and-true path, LiteRemote can also drive Windows' own **Remote Desktop (RDP)** from
inside the same window — so one app covers both a lean custom protocol *and* native RDP.

- **Lightweight** — the host runs headless in the system tray; both apps ship as self-contained,
  single-file executables (no separate .NET runtime install needed).
- **Fast** — GPU capture, H.264 hardware encoding with a JPEG fallback, an adaptive frame-rate
  controller, and TCP `NoDelay` streaming.
- **Secure** — TLS 1.2/1.3 with public-key certificate pinning (TOFU), Argon2id password hashing, and
  optional Google sign-in.
- **Flexible** — connect by IP address, by a short 9-digit ID through a relay, or via embedded Windows
  RDP.

---

## Features

### Three ways to connect
| Mode | How you reach the remote PC | Best for |
|---|---|---|
| **LiteRemote by address** | Enter the host's IP/hostname and port (default **7443**). Peer-to-peer, no server in the middle. | LAN, VPN, or a machine with a reachable address / port-forward. |
| **LiteRemote by ID** | Enter a **9-digit ID** shown on the remote PC. A lightweight **relay** introduces the two peers so neither side needs to know the other's IP. | Reaching a PC behind NAT without port-forwarding. |
| **Windows RDP (embedded)** | The native Microsoft Remote Desktop client, hosted inside LiteRemote. Signs in with a Windows account and can pass the login screen. Requires Remote Desktop enabled on the target (port **3389**). | Standard Windows RDP, unattended servers, the login/lock screen. |

### Everything else
- **Strong transport security** — TLS 1.2/1.3 on every connection; the host uses a persistent
  self-signed ECDSA P-256 certificate, and the viewer pins its public-key SHA-256 on first connect
  (trust-on-first-use), rejecting any later key change as a possible man-in-the-middle.
- **Two sign-in methods** — an access **password** (stored only as an Argon2id hash, never plaintext)
  **or** **Google sign-in** (OAuth PKCE; the returned `id_token` is verified offline against Google's
  keys plus an allow-list).
- **H.264 hardware encoding with JPEG fallback** — negotiates hardware H.264 (Media Foundation) when
  the host supports it and automatically falls back to a dirty-tile JPEG encoder otherwise.
- **Live tuning during a session** — change **FPS**, **resolution preset**, and **quality** from the
  session toolbar and see the effect immediately, along with live FPS / resolution stats.
- **Two-way clipboard sync** — copy and paste text, images, and file lists between the two machines.
- **Multiple sessions at once** — each connection opens in its own window, so you can control several
  machines side by side.
- **Optional per-app split-tunnel VPN** — route *only* this app's connection through an OpenVPN
  `.ovpn` profile (a bundled, signed OpenVPN engine), leaving the rest of your PC on the normal
  network. Handy for reaching an RDP host that's only accessible over VPN.
- **Host privacy controls** — optionally blank the host's physical screen and lock its local keyboard
  and mouse during a session.
- **Clean, modern desktop UI** — a polished blue-accented interface with a **dark / light / follow-system
  theme** and **English / Indonesian** language, both switchable live. A "Recent & Saved" list gives
  one-click reconnect (credentials can be remembered, encrypted per Windows account via DPAPI), and any
  saved session's account/VPN credentials can be edited in place.

---

## Download & install

1. Go to the [**latest release**](https://github.com/amirullah/lite-remote-desktop/releases/latest).
2. Download the **`LiteRemote-Setup-<version>.exe`** installer and run it.

The single installer sets up **both** components:

- **LiteRemote** — the *Viewer*, for the PC you sit at.
- **LiteRemote Host** — the tray app, for the PC you want to control.

During setup you can optionally start the Host at Windows logon and add a Windows Firewall rule that
allows incoming host connections on **port 7443** (recommended if you plan to use by-address
connections). Portable ZIPs of the Viewer, Host, and Relay are also attached to each release if you'd
rather not run an installer.

> Install a component only where you need it: the Viewer on the controlling PC, the Host on the PC being
> controlled. Both can coexist on the same machine.

---

## Quick start

### On the PC you want to control (Host)
1. Launch **LiteRemote Host**. Its icon appears in the system tray.
2. Right-click the tray icon → **Set access password**. (Or configure Google sign-in.)
3. Open the tray status window to see the host's **certificate fingerprint**. Share it out-of-band with
   whoever will connect, so they can verify it once on first connection.
4. For by-address connections, make sure **TCP port 7443** is allowed through the firewall (the
   installer can add this rule for you).

### On the controlling PC (Viewer)
1. Launch **LiteRemote**.
2. Choose a **connection type**:
   - **LiteRemote · address** — enter the **host address** and **port** (default 7443).
   - **LiteRemote · ID** — enter the **9-digit ID** (and set the relay address under *Advanced*).
   - **Windows RDP** — enter the target's address plus a Windows username/password.
3. For LiteRemote modes, pick **Password** or **Google account** and enter your credentials.
4. (Optional) tick **Connect through VPN** to bring up a split-tunnel OpenVPN profile for this host, and
   use **Advanced** for the relay (ID mode) or session options (clipboard sync / host privacy).
5. Click **Connect**. On the first connection, confirm the certificate fingerprint matches the host,
   then trust it.

### Using Windows RDP mode
Enable **Remote Desktop** on the target PC (System → Remote Desktop) and make sure **TCP port 3389** is
reachable. RDP mode uses a Windows account and can operate at the login/lock screen — no LiteRemote
Host required on the target.

---

## How it works

```
Viewer (LiteRemote, WPF)                                   Host (LiteRemoteHost, tray)
  • Decode H.264 / JPEG tiles     ── TLS 1.3 (pinned) ──▶     • Desktop Duplication / GDI capture
  • Render to WriteableBitmap                                 • H.264 (Media Foundation) / JPEG encode
  • Inject input, bridge clipboard  ◀── video + stats ──      • SendInput injection
  • Optional split-tunnel VPN       ── input / clipboard ─▶    • Clipboard + privacy controls
```

- **Direct (by-address) and Windows RDP are peer-to-peer.** Traffic goes straight from the viewer to
  the host — no third-party server is involved.
- **The relay is used only for ID mode.** It simply introduces two peers and forwards bytes that are
  already end-to-end TLS-encrypted; it cannot read your screen, input, or clipboard. Certificate
  pinning still applies (pinned by ID), and each host protects its ID with a per-host secret so another
  machine can't claim it. You can run your own relay on any small VPS.

### Running your own relay (for ID mode)
The relay is a tiny standalone server (`RemoteDesktop.Relay`), published for both Windows and Linux and
attached to each release. On your VPS, open a port (default **7500**) and run it:

```bash
./RemoteDesktop.Relay 7500
```

Then point both the Host (tray → set up ID access) and the Viewer (*Advanced → Relay server*) at
`your-vps-address:7500`.

### Ports at a glance
| Port | Protocol | Used by | Direction |
|---|---|---|---|
| **7443** | TCP | LiteRemote host listener (by-address mode) | Inbound to the host |
| **7500** | TCP | Relay server (ID mode) | Host & viewer connect outbound |
| **3389** | TCP | Windows Remote Desktop (RDP mode) | Inbound to the target |

---

## Security notes

- **TLS 1.2/1.3** on all LiteRemote traffic; persistent self-signed ECDSA P-256 host certificate.
- **Certificate pinning (TOFU):** the viewer stores the host's public-key SHA-256 on first connect and
  refuses to continue if it ever changes.
- **Passwords** are stored as memory-hard **Argon2id** hashes — never in plaintext.
- **Google sign-in** verifies the `id_token` offline against Google's JWKS plus an email allow-list.
- **Source restrictions:** optional CIDR allow-list and binding the host listener to a specific
  address/interface (e.g. a VPN adapter only).
- **Remembered credentials** are encrypted with Windows **DPAPI**, scoped to your Windows user account.

Verify the host fingerprint out-of-band on first use, keep the Host up to date, and only expose port
7443 to networks you trust (or reach it over VPN / ID mode instead of a public port-forward).

---

## Build from source

**Prerequisites:** Windows 10/11, the [.NET 8 SDK](https://dotnet.microsoft.com/download), and the
Windows desktop workload (WPF/WinForms). The Host and Client target `net8.0-windows`, so they must be
built on Windows; the `Shared` and `Relay` projects are portable.

```powershell
git clone https://github.com/amirullah/lite-remote-desktop.git
cd lite-remote-desktop
dotnet build -c Release
```

Run each component during development:

```powershell
# Host (tray app) — set an access password from the tray after it starts
dotnet run --project src/RemoteDesktop.Host   -c Release

# Viewer (WPF) — connect to 127.0.0.1:7443 to test against a local host
dotnet run --project src/RemoteDesktop.Client -c Release

# Relay (optional, only needed for ID mode) — listen on port 7500
dotnet run --project src/RemoteDesktop.Relay  -c Release 7500
```

Build the installer and the full package set (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
pwsh -File build-installer.ps1
```

This publishes the Viewer, Host, and Relay as self-contained single files, bundles the signed OpenVPN
engine for split-tunnel VPN, and produces the `LiteRemote-Setup-<version>.exe` installer plus portable
ZIPs. (CI builds the same artifacts on every push and publishes a GitHub Release when you push a `v*`
tag.)

### Project layout
LiteRemote is four .NET 8 projects:

- **`RemoteDesktop.Shared`** — wire protocol (framing, binary message codec), data models, and crypto
  (Argon2id, certificate pinning, Google `id_token` verification, the `MessageChannel` transport).
- **`RemoteDesktop.Host`** — the controlled machine: screen capture, adaptive H.264/JPEG encoding, input
  injection, clipboard, TLS server, and the tray UI. Produces `LiteRemoteHost.exe`.
- **`RemoteDesktop.Client`** — the WPF viewer, including embedded Windows RDP and the split-tunnel VPN.
  Produces `LiteRemote.exe`.
- **`RemoteDesktop.Relay`** — the tiny rendezvous server used for ID connections.

---

## Contributing

Issues and pull requests are welcome. Please keep changes focused, build in `Release` before opening a
PR, and describe how you tested. For anything security-related, prefer a private report over a public
issue.

## License

Released under the [MIT License](LICENSE).
