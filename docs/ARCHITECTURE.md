# Arsitektur LiteRemote

## Tujuan desain

1. **Latensi rendah dulu, throughput kedua.** Interaktivitas (mouse/keyboard) harus terasa instan,
   jadi `TCP_NODELAY` aktif, input dikirim sebagai pesan mungil fixed-size, dan video boleh di-*drop*
   di bawah tekanan alih-alih menumpuk antrean.
2. **Kirim sesedikit mungkin.** Layar diam = ~0 byte. Hanya tile yang berubah yang dikirim.
3. **Tanpa dependensi native berat.** Baseline codec memakai JPEG (GDI+/WIC) yang selalu tersedia;
   H.264 hardware bersifat opsional dan dinegosiasikan.
4. **Aman secara default.** Tidak ada mode "tanpa enkripsi". Tidak ada auth = tolak semua.

## Lapisan

```
Application  │ Host: capture→encode→inject→clipboard→privacy   Client: decode→render→input→clipboard
Protocol     │ MessageType + framing + PayloadCodec/VideoFrameCodec/ClipboardCodec/AuthProtocol
Transport    │ MessageChannel (framed, full-duplex, bounded channels)
Security     │ SslStream (TLS 1.2/1.3) + CertificateManager (pin) + PasswordHasher + Google verify
Network      │ TCP (NoDelay). Socket bisa di-bind ke IP tunnel VPN.
```

## Format wire

Setiap pesan: `[1B type][4B length LE][payload]`. Lihat `Framing`.

- **Hot path (input, video):** serializer biner tangan (`PayloadCodec`, `VideoFrameCodec`) — beberapa
  byte overhead, nol refleksi.
- **Cold path (auth, settings, display list, stat):** biner ringkas atau JSON (auth) karena jarang.

### Video: dirty-tile

Layar dibagi grid `TileSize` (default 128px). Tiap frame:
1. Capture (BGRA) dari Desktop Duplication atau GDI.
2. Untuk tiap tile: bandingkan 8 byte sekaligus vs. state terkirim terakhir.
3. Tile berubah → encode JPEG (kualitas dari `AdaptiveController`).
4. Kirim `VideoFrame` = kumpulan tile `{x,y,w,h,jpeg}`.

Client menyimpan `WriteableBitmap` persisten dan hanya menimpa tile yang diterima → hemat CPU/GPU.

Keyframe (semua tile) dikirim saat: connect, ganti monitor, resize, `KeyFrameRequest`, atau setelah
frame ter-*drop* (agar tak ada region basi).

### Kontrol frame rate

`AdaptiveController` (Auto):
- Batas atas = min(`MaxFps`, refresh monitor).
- `encodeBoundFps` = 1000 / (EMA waktu encode × 1.2) → jangan jadwalkan lebih cepat dari kemampuan encode.
- `linkBoundFps` = kapasitas link (Mbps) / ukuran frame rata-rata.
- Target = min(ketiganya), di-*ease* agar tak berosilasi.
- Di bawah tekanan: turunkan **kualitas** dulu (motion tetap mulus), fps belakangan.

Mode Fixed: fps mengikuti pilihan user apa adanya.

## Threading

**Host**
- Accept loop (async) → per-klien `HostSession`.
- `MessageLoopAsync` (async) — memroses input/clipboard/settings/keyframe.
- `CaptureLoop` (thread khusus) — capture+encode+send, dipacu `AdaptiveController.FrameInterval`.
- `ClipboardService` & `HostPrivacyService` — masing-masing thread STA dengan message pump.

**Client**
- UI thread (WPF Dispatcher) — render, input, clipboard.
- Message loop (Task) — baca socket, decode JPEG tile (di sini, off-UI), lalu `BeginInvoke` blit satu-pass.

## Manajemen memori

- Outbound video frame di-*rent* dari `ArrayPool` dan dikembalikan oleh write-pump setelah terkirim
  (atau oleh `HostSession` bila frame di-drop).
- Inbound payload dialokasi normal (ukuran pas) lalu di-GC — dikonsumsi sinkron sebelum pesan berikutnya.
- Encoder host memakai ulang buffer `previous`/bitmap tile antar frame.

## Titik ekstensi

- **Codec baru:** implement encoder yang menghasilkan `Tile.Data` (mis. NAL H.264) dan tambah entri
  di `VideoCodec`; negosiasi lewat `SessionSettings.PreferredCodec` + `VideoConfig.Codec`.
- **Capture baru:** implement `IScreenCapture` (mis. Windows.Graphics.Capture untuk window tunggal).
- **Auth baru:** tambah `AuthMethod` + cabang verifikasi di `HostSession.AuthenticateAsync`.
