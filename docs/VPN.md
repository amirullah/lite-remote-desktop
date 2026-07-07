# VPN per-aplikasi (OpenVPN)

Tujuan: **hanya koneksi remote desktop** yang lewat VPN, sementara sisa lalu lintas komputer tetap
memakai internet biasa. LiteRemote mencapai ini **tanpa driver kernel**.

## Cara kerja

`VpnService.StartAsync(profile.ovpn, host)` melakukan:

1. **Resolve** alamat IP host tujuan.
2. **Jalankan `openvpn.exe`** dengan:
   - `--config <profile.ovpn>`
   - `--route-nopull` → abaikan route yang dipush server (jangan bajak default route).
   - `--route <hostIP> 255.255.255.255 vpn_gateway` → arahkan **hanya** host tujuan lewat tunnel.
3. **Deteksi adapter tunnel** (TAP/WinTUN/OpenVPN) yang baru naik dan ambil IPv4 lokalnya.
4. **Bind socket** koneksi ke IP tunnel itu (`RemoteConnection.ConnectAsync(..., bindAddress)`).

Karena hanya socket kita yang bersumber dari IP tunnel, dan hanya IP host yang diroute lewat tunnel,
tidak ada aplikasi lain yang terpengaruh.

```
                   ┌──────────────── PC Client ────────────────┐
   Browser, dll ──▶│ default route ─────────────▶ internet     │
                   │                                            │
   LiteRemote ────▶│ socket bind → tun IP ─▶ OpenVPN ─▶ Host    │
                   └────────────────────────────────────────────┘
```

## Prasyarat

- **OpenVPN Community** terpasang (atau `openvpn.exe` yang di-*bundle*). Default: dicari di `PATH`.
  Bisa juga: `new VpnService(@"C:\Program Files\OpenVPN\bin\openvpn.exe")`.
- Menaikkan tunnel biasanya butuh hak admin (instalasi driver TAP/WinTUN & modifikasi route). Jalankan
  client sebagai administrator, atau gunakan layanan OpenVPN interaktif.
- Profil `.ovpn` yang valid (beserta kredensial/sertifikatnya).

## Direct vs VPN

Di UI client, **Network path**:
- **Direct internet / LAN** — tanpa VPN. Untuk LAN atau host yang punya port-forward/DDNS.
- **Through VPN profile (this app only)** — pilih file `.ovpn`; hanya app ini yang lewat tunnel.

## Batasan & opsi lanjutan

- Pendekatan bind+route mengarahkan berdasarkan **IP tujuan**. Jika banyak aplikasi menuju IP host
  yang sama, semuanya ikut lewat tunnel. Untuk skenario kita (hanya LiteRemote yang bicara ke host),
  efeknya identik dengan "app-only".
- **App-scoped penuh** (semua trafik aplikasi apa pun tujuannya) memerlukan filtering per-proses via
  **Windows Filtering Platform (WFP)** callout driver atau fitur *per-app VPN* Windows (MDM). Ini di
  luar cakupan versi ini tetapi merupakan titik ekstensi yang jelas.
- WireGuard: pola yang sama berlaku — naikkan interface WireGuard, bind ke IP-nya, route host `/32`.

## Pengujian

1. Sebelum connect: `route print` → catat default route.
2. Connect via VPN profile.
3. `route print` lagi → harus muncul route `/32` ke host lewat gateway tunnel; default route tak berubah.
4. `Get-NetTCPConnection -RemotePort 7443` (PowerShell) → `LocalAddress` harus IP tunnel.
