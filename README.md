# LiteRemote

**Aplikasi remote desktop Windows yang ringan, sederhana, namun powerful.**

LiteRemote dirancang untuk terasa instan: menangkap layar host lewat GPU (Desktop Duplication),
hanya mengirim bagian layar yang **berubah** (dirty-tile), dan mengatur frame rate secara otomatis
sesuai bandwidth dan CPU. Koneksi diamankan dengan TLS + certificate pinning, dan bisa berjalan
lewat internet langsung **atau** lewat profil VPN (OpenVPN) yang hanya dipakai oleh aplikasi ini.

> Status: implementasi referensi yang lengkap dan terstruktur. Semua alur inti (capture → encode →
> stream → decode → render, input, clipboard, auth, VPN split-route) sudah ada. Lihat
> [Roadmap](#roadmap--ide-tambahan) untuk item lanjutan (H.264 hardware, audio, transfer file).

---

## Daftar fitur (sesuai permintaan)

| Permintaan | Implementasi |
|---|---|
| Ringan, sederhana, powerful | Capture GPU + hanya kirim tile yang berubah; encoder JPEG per-tile tanpa dependensi native berat. Host jalan di tray, headless. |
| Frame rate tinggi, bisa dipilih & otomatis | Mode **Automatic** (adaptif ke bandwidth/CPU/refresh monitor) atau **Fixed** (15–144 fps). Lihat `AdaptiveController`. |
| Pilih resolusi, display, auto-menyesuaikan monitor | Pilih monitor host (`DisplayList`), mode resolusi **Native / Scaled / Match-client**. |
| Copy-paste apa pun dari remote | Sinkronisasi clipboard dua arah: teks, gambar (PNG), dan daftar file. |
| Benar-benar ringan & cepat | Diff 8-byte per tile, `ArrayPool`, TCP `NoDelay`, decode JPEG off-thread, blit satu-pass ke `WriteableBitmap`. |
| Login manual atau Google | Password (Argon2id) **atau** Login dengan Google (OAuth PKCE + verifikasi id_token offline). |
| Remote lewat **ID** (ala TeamViewer) | Host mendaftar ID 9-digit ke **relay server**; viewer cukup masukkan ID + password — tanpa tahu IP/port. Enkripsi tetap **end-to-end**, relay hanya meneruskan byte. |
| VPN per-aplikasi (OpenVPN) atau internet langsung | `VpnService` menaikkan tunnel OpenVPN & mem-bind socket ke IP tunnel → hanya koneksi ini yang lewat VPN. |
| Keamanan tinggi | TLS 1.2/1.3, pinning kunci publik (TOFU), Argon2id, allow-list email/CIDR, blank screen & lock input host. |

---

## Arsitektur singkat

```
┌─────────────────────────┐         TLS 1.3 (pinned)          ┌──────────────────────────┐
│   LiteRemote (Client)    │  ───────────────────────────────▶ │  LiteRemoteHost (Host)   │
│   WPF viewer             │                                    │  Tray app, headless      │
│                          │  ◀───────────────────────────────  │                          │
│  • Decode JPEG tiles     │   video (dirty tiles), stats       │  • Desktop Duplication   │
│  • WriteableBitmap       │                                    │    / GDI fallback        │
│  • Input → normalized    │   input, settings, clipboard       │  • Dirty-tile encoder    │
│  • Clipboard bridge      │ ─────────────────────────────────▶ │  • SendInput injection   │
│  • Google OAuth / VPN    │                                    │  • Clipboard + privacy   │
└─────────────────────────┘                                    └──────────────────────────┘
```

Tiga proyek .NET 8:

- **`RemoteDesktop.Shared`** — protokol wire (framing, message, codec biner), model, kripto
  (Argon2id, certificate pinning, verifikasi Google id_token), transport `MessageChannel`.
- **`RemoteDesktop.Host`** — mesin yang dikontrol. Capture, encode adaptif, injeksi input, clipboard,
  server TLS, tray UI. Output: `LiteRemoteHost.exe`.
- **`RemoteDesktop.Client`** — viewer WPF. Output: `LiteRemote.exe`.

Detail lengkap di [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Build

Prasyarat: **Windows 10/11**, **.NET 8 SDK**, workload desktop (`Microsoft.NET.Sdk.WindowsDesktop`).

```powershell
git clone <repo> && cd remote-desktop
dotnet restore
dotnet build -c Release

# Publish single-file (host):
dotnet publish src/RemoteDesktop.Host -c Release -r win-x64 --self-contained false
# Client:
dotnet publish src/RemoteDesktop.Client -c Release -r win-x64 --self-contained false

# (Opsional) aktifkan capture GPU Desktop Duplication (butuh package Vortice):
dotnet build src/RemoteDesktop.Host -c Release -p:EnableDxgi=true
```

> **Capture backend.** Default memakai **GDI BitBlt** yang selalu tersedia dan sudah ringan berkat
> dirty-tile diffing. Jalur **Desktop Duplication (DXGI)** — CPU lebih rendah untuk layar 4K/refresh
> tinggi — bersifat opt-in via `-p:EnableDxgi=true` karena API Vortice sedikit berbeda antar versi.

> Proyek Host & Client menargetkan `net8.0-windows` (WPF/WinForms + P/Invoke), jadi **harus dibuild
> di Windows**. Proyek `Shared` murni `net8.0` dan portabel.

---

## Cara pakai

### Di komputer yang mau dikontrol (Host)
1. Jalankan `LiteRemoteHost.exe`. Ikon muncul di tray.
2. Klik kanan tray → **Set access password** (atau **Configure Google login**).
3. Klik dua kali tray → **Show status** untuk melihat **fingerprint sertifikat**. Catat/bacakan ini
   ke orang yang akan connect (verifikasi out-of-band, sekali saja).

### Di komputer pengontrol (Client)
1. Jalankan `LiteRemote.exe`.
2. Isi **Host address** + **Port** (default 7443).
3. Pilih **Password** atau **Google account**.
4. Pilih **Network path**: *Direct* atau *Through VPN profile* (pilih file `.ovpn`).
5. Atur **frame rate / resolusi / kualitas** bila perlu → **Connect**.
6. Pada koneksi pertama, cocokkan fingerprint dengan yang di host, lalu **Trust**.

---

## Remote lewat ID (ala TeamViewer)

Agar tidak perlu tahu IP atau setup port-forward, LiteRemote punya **relay/rendezvous server**
ringan (`RemoteDesktop.Relay`, ~1 file) yang bisa Anda jalankan di VPS kecil mana pun:

```bash
# di VPS (Linux/Windows), buka port 7500:
./LiteRemoteRelay 7500
```

Lalu:
1. **Host** → tray → *Set up ID access (relay)* → isi `alamat-vps:7500`. Host menampilkan **ID 9-digit**.
2. **Viewer** → tab **Connect by ID** → masukkan ID + password → **Connect**.

Relay **hanya menyambungkan dua socket** dan meneruskan lalu lintas yang **sudah terenkripsi TLS**
end-to-end; ia tidak bisa membaca layar, input, atau clipboard. Certificate pinning tetap berlaku
(di-pin berdasarkan ID). ID dilindungi *secret* per-host agar tidak bisa diklaim mesin lain.

## Keamanan

- **TLS 1.2/1.3** untuk semua trafik. Host memakai sertifikat ECDSA P-256 self-signed yang persisten.
- **Certificate pinning (TOFU):** client menyimpan SHA-256 kunci publik host saat pertama connect;
  perubahan kunci (indikasi MITM) langsung ditolak.
- **Password:** disimpan sebagai hash **Argon2id** (memory-hard), tidak pernah plaintext.
- **Google login:** id_token diverifikasi **offline** terhadap JWKS Google + allow-list email.
- **Pembatasan sumber:** opsi allow-list **CIDR** dan bind ke alamat/interface tertentu (mis. VPN saja).
- **Privasi host:** *blank screen* & *lock local input* selama sesi berlangsung.

Detail & threat model: [`docs/SECURITY.md`](docs/SECURITY.md).

---

## VPN per-aplikasi

Alih-alih memaksa seluruh PC lewat VPN, LiteRemote hanya mengarahkan **koneksi remote ini** melalui
tunnel OpenVPN: profil `.ovpn` dinaikkan dengan `--route-nopull` + route host `/32` lewat tunnel,
lalu socket koneksi di-bind ke IP tunnel. Aplikasi lain tetap memakai internet biasa.

Cara kerja, batasan, dan opsi lanjutan (WFP app-scoped): [`docs/VPN.md`](docs/VPN.md).

---

## Roadmap & ide tambahan

Lihat [`docs/IDEAS.md`](docs/IDEAS.md) untuk daftar lengkap. Sorotan:

- **Codec H.264/H.265 hardware** (Media Foundation / NVENC) — negosiasi otomatis, hemat bandwidth besar.
- **Transfer audio** (WASAPI loopback) & **transfer file drag-drop**.
- **Multi-monitor sekaligus** dan **wake-on-LAN**.
- **Relay/rendezvous server** untuk NAT traversal tanpa port-forward (mirip cara kerja RustDesk).
- **Session recording**, **watermark**, **2FA (TOTP)**, dan **unattended access** sebagai service.

---

## Lisensi

Lihat berkas `LICENSE` (tambahkan sesuai kebutuhan Anda).
