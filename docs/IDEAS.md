# Ide tambahan & roadmap

Fitur di bawah dipilih agar selaras dengan tujuan: **ringan, cepat, aman**. Diurutkan kasar dari
dampak-tertinggi/effort-terendah ke yang lebih besar.

## Performa & kualitas gambar
- **Codec H.264/H.265 hardware** (Media Foundation, NVENC/QuickSync/AMF). Negosiasi otomatis; turun
  ke JPEG-tile bila tak didukung. Ini penghematan bandwidth terbesar untuk konten bergerak/video.
- **Encoder tile paralel** — encode tile berubah di beberapa core (`Parallel.For`) untuk 4K.
- **Region-of-interest / adaptif kualitas** — kualitas lebih tinggi di sekitar kursor/area aktif.
- **Damage dari Desktop Duplication** — pakai `GetFrameDirtyRects`/`MoveRects` agar diff makin murah.
- **Kompresi LZ4** untuk tile teks (sudah ada dependensi K4os.LZ4 di Shared) sebagai alternatif JPEG.

## Interaksi
- **Audio dua arah** — WASAPI loopback capture di host (Opus) + mikrofon client.
- **Transfer file** — drag-drop & panel file manager di atas kanal terenkripsi yang sama.
- **Multi-monitor simultan** — tampilkan beberapa monitor sekaligus, bukan hanya satu.
- **Sinkron kursor bentuk** — kirim cursor shape terpisah agar kursor mulus tanpa full-frame.
- **Touch & pen passthrough**, **shortcut kustom**, **mode "view only"**.

## Jaringan & konektivitas
- **Relay/rendezvous server** untuk NAT traversal tanpa port-forward (ICE/STUN/TURN, model RustDesk).
- **Transport QUIC/UDP** dengan FEC untuk link lossy (lebih baik dari TCP saat packet loss).
- **Wake-on-LAN** untuk menyalakan host dari jauh.
- **ID + kata sandi sekali pakai** (mode dukungan pelanggan) selain koneksi berbasis alamat.

## Keamanan & manajemen
- **2FA (TOTP)** di atas password.
- **Unattended access sebagai Windows Service** + capture secure desktop (helper `uiAccess`/`SendSAS`).
- **Rate-limit & lockout** login; **audit log** koneksi (siapa, kapan, dari mana).
- **Session recording** (opsional, dengan indikator jelas) & **watermark identitas**.
- **PAKE (SRP/OPAQUE)** agar password tak pernah dikirim walau di dalam TLS.
- **Kebijakan perangkat** — daftar device tepercaya, kadaluarsa pin, rotasi sertifikat terjadwal.

## Kualitas hidup
- **Auto-reconnect** dengan resume token yang sudah ada di `AuthResult`.
- **Profil koneksi** tersimpan (sudah ada dasar `SavedConnection`), folder & tag.
- **Tema terang/gelap**, **skala DPI per-monitor** yang lebih pintar.
- **Statistik grafik** (bandwidth/fps/latency) real-time.
- **Instalasi MSI/winget** + auto-update yang ditandatangani.

## Lintas platform (jangka panjang)
- Client **macOS/Linux/Web (WebRTC/WASM)** memakai protokol yang sama; host tetap Windows.
- Aplikasi **mobile** (Android/iOS) sebagai viewer.
