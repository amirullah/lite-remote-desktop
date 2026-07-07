# Keamanan LiteRemote

## Ringkasan kontrol

| Lapisan | Kontrol |
|---|---|
| Transport | TLS 1.2/1.3 wajib (`SslStream`), tidak ada mode plaintext. |
| Identitas host | Sertifikat ECDSA P-256 self-signed persisten (`CertificateManager`). |
| Anti-MITM | Certificate pinning kunci publik (TOFU) di sisi client (`PinStore`). |
| Auth password | Argon2id (memory-hard, 64 MiB, t=3, p=2), verifikasi konstan-waktu. |
| Auth Google | Verifikasi RS256 id_token vs JWKS Google + cek `iss`/`aud`/`exp` + allow-list email. |
| Otorisasi jaringan | Allow-list CIDR sumber + bind address (mis. hanya interface VPN). |
| Privasi sesi | Blank screen host + block input lokal selama dikontrol. |
| Ketersediaan | Framing dibatasi `MaxPayloadSize` (64 MiB) agar peer nakal tak menghabiskan memori. |

## Model handshake

```
Client ──TCP+TLS──▶ Host        (client memverifikasi & mem-pin kunci publik host)
Host   ──AuthRequest(methods, nonce)──▶ Client
Client ──AuthResponse(method, secret)──▶ Host
Host   ──AuthResult(ok, token)──▶ Client
```

- **Password:** dikirim di dalam terowongan TLS terenkripsi, diverifikasi Argon2id di host. Karena
  TLS + pinning mencegah MITM, plaintext tidak pernah terekspos di jaringan.
- **Google:** client mengambil `id_token` via OAuth PKCE loopback, host memverifikasinya **tanpa**
  menyimpan rahasia apa pun (hanya percaya tanda tangan Google + allow-list email).

## Kenapa pinning, bukan CA publik?

Host adalah PC pribadi, bukan layanan ber-domain. Menerbitkan sertifikat CA publik tidak praktis.
Pinning kunci publik memberi jaminan yang setara/lebih kuat untuk skenario ini: setelah user
memverifikasi fingerprint sekali (out-of-band, lewat dialog tray host), setiap upaya penyusupan
dengan kunci berbeda akan gagal keras.

**Verifikasi fingerprint itu penting.** Cocokkan string di dialog *Trust this host?* pada client
dengan *Show status* di tray host melalui kanal tepercaya (telepon/tatap muka) saat pertama connect.

## Praktik pengerasan yang disarankan

1. **Selalu set auth kuat.** Host menolak semua koneksi jika tak ada password/Google yang dikonfigurasi.
2. **Batasi paparan jaringan.** Untuk akses jarak jauh, utamakan jalur **VPN** dan set
   `BindAddress`/`AllowedClientCidrs` ke subnet VPN saja — host tak perlu terekspos ke internet publik.
3. **Password panjang & acak** untuk manual login; pertimbangkan Google login untuk audit per-identitas.
4. **Rotasi sertifikat** dengan menghapus `host.pfx` (client akan diminta verifikasi ulang).
5. **Jalankan host dengan privilege minimal.** Elevasi hanya diperlukan untuk menangkap secure desktop
   (UAC/lock screen) dan `BlockInput` penuh; jalankan helper terelevasi terpisah bila butuh.

## Batasan yang diketahui / TODO keamanan

- **Ctrl+Alt+Del / secure desktop:** injeksi SendInput biasa tidak memicu Secure Attention Sequence.
  Diperlukan helper ber-`uiAccess`/service + `SendSAS` untuk dukungan penuh.
- **Rate-limiting auth:** tambahkan backoff/lockout untuk mempersulit brute-force online.
- **Perfect forward secrecy** sudah didapat dari cipher suite TLS 1.3; pastikan menonaktifkan suite lama.
- **Replay nonce** dari `AuthRequest` saat ini informatif; untuk PAKE penuh pertimbangkan SRP/OPAQUE.

Laporkan isu keamanan secara privat sebelum publik.
