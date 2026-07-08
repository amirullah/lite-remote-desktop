# Rencana codec H.264 (lompatan performa PC fisik)

Status per 2026-07-08. Tujuan: codec video **antar-frame** dengan **encode hardware** agar sesi
LiteRemote antar PC fisik setara TeamViewer/Parsec (bandwidth jauh lebih hemat, 60 fps mulus).
JPEG-tiles tetap fallback universal (VM tanpa encoder hardware, atau negosiasi gagal).

## Kenapa ini lompatan (bukan JPEG)
- JPEG-tiles = kirim potongan gambar per-frame (intra-frame). Boros bandwidth saat gerakan.
- H.264 = kompresi **antar-frame** (hanya kirim beda antar frame) + **encode di GPU** (nyaris nol CPU).
  Di PC fisik: bandwidth turun drastis, fps naik, latensi encode ~1-3 ms.
- CATATAN: **tidak** membantu VM — VM tak punya encoder hardware, dan capture VM tetap ~72 ms
  (lantai VMware, sudah dibuktikan). H.264 murni untuk target PC fisik.

## Milestone
- [x] **M0 — Probe** (`--h264-probe`, sudah di main): konfirmasi MFT encoder H.264.
  Terbukti di PC AMD: 2× `AMDh264Encoder` (hardware) + 1 software. Interop MF dasar bekerja.
- [x] **M1 — Encoder host** (`H264Encoder.cs` + `--bench-h264`) — SELESAI & terbukti:
  - PC AMD fisik: `AMDh264Encoder [hardware/async]` dipilih otomatis, Annex-B **VALID**,
    ~33 KB/frame, encode best ~8 ms (feed+drain sinkron; GPU murni lebih cepat, ter-pipeline
    di produksi). NV12 diparalelkan (1080p ~9→~2 ms). Fallback SW-sync bila tak ada hardware.
  - Detail asli milestone: 
  - Paket `Vortice.MediaFoundation` (v3.6.2, sudah terverifikasi restore).
  - MFTEnumEx(VIDEO_ENCODER, HARDWARE→fallback SW) → `IMFTransform`.
  - SetOutputType: H264 (MF_MT_AVG_BITRATE, MF_MT_FRAME_SIZE, MF_MT_FRAME_RATE,
    MF_MT_INTERLACE_MODE=Progressive, profil Main/Baseline).
  - SetInputType: **NV12** (butuh konversi BGRA→NV12; lakukan di CPU ~5-15 ms/1080p,
    atau via Video Processor MFT).
  - Loop ProcessInput/ProcessOutput → kumpulkan NAL unit (Annex-B). Tangani keyframe
    (MFSampleExtension_CleanPoint) + minta IDR berkala (CODECAPI_AVEncVideoForceKeyFrame).
  - Config low-latency: `CODECAPI_AVLowLatencyMode=true`, `AVEncCommonRateControlMode=CBR`,
    tanpa B-frame.
  - Uji `--bench-h264`: capture beberapa frame, encode, verifikasi ada start-code NAL +
    ukuran + waktu. Jalankan di PC fisik (harus hardware) — target enc < 5 ms.
- [x] **M2 — Decoder client** (`H264Decoder.cs`) — SELESAI & terbukti:
  - `Microsoft H264 Video Decoder MFT`, NV12→BGRA (paralel), tangani MF_E_TRANSFORM_STREAM_CHANGE.
  - Round-trip di `--bench-h264`: 149/150 frame ter-decode, semua gambar valid (non-hitam),
    decode ~4 ms/frame. Codec terbukti dua arah pada satu mesin.
  - Catatan integrasi: kelas ada di proyek Host untuk uji; M4 memindah/mereferensikan ke Client.
  - Detail asli milestone: 
  - `IMFTransform` decoder H.264 (DXVA hardware bila ada) → NV12 → RGB → WriteableBitmap.
  - Atau MediaEngine/SampleGrabber. Rakit ulang NAL menjadi sample; suapi decoder.
- [ ] **M3 — Protokol & negosiasi**:
  - `VideoCodec.H264` (sudah ada di enum). Host kirim SPS/PPS + frame via `MessageType.VideoFrame`
    (atau tipe baru `H264Frame`). `VideoConfig.Codec=H264` saat encoder tersedia & klien dukung.
  - Fallback otomatis ke JpegTiles bila init encoder/decoder gagal (WAJIB — jangan sampai
    aplikasi rusak).
- [ ] **M4 — Integrasi**:
  - `HostSession.CaptureLoop`: pilih H264Encoder vs JpegTileEncoder berdasarkan negosiasi.
  - `FrameSurface`/`RemoteConnection`: jalur render H.264.
  - Adaptif: bitrate mengikuti AdaptiveController (bukan quality JPEG).

## Risiko / catatan
- Interop MF luas — pakai Vortice.MediaFoundation, bangun bertahap, uji tiap milestone dgn bench.
- Async hardware MFT (event-driven) lebih rumit dari sync SW MFT — mulai dari SW MFT untuk
  membuktikan pipeline, lalu naik ke hardware async untuk kecepatan penuh.
- BGRA→NV12 adalah biaya CPU baru; pertimbangkan konversi di GPU (shader) kelak.
- Selalu jaga jalur JPEG-tiles tetap default sampai H.264 terbukti stabil end-to-end.

## Cara uji cepat
- `LiteRemoteHost --h264-probe` — cek ketersediaan encoder (sudah ada).
- `LiteRemoteHost --bench-h264` — (M1) ukur encode H.264 lokal.
- Uji ke VM 192.168.1.5 via viewer untuk membandingkan (walau VM pakai fallback JPEG).
