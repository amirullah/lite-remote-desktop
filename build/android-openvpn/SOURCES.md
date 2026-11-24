# libovpnexec.so — sumber GPL (OpenVPN 2.6, fork ics-openvpn)

`libovpnexec.so` (arm64-v8a) adalah executable OpenVPN 2.x (`TARGET_ANDROID`) yang dibangun oleh
`.github/workflows/android-openvpn.yml` + `build/android-openvpn/build.sh`. Lisensi: **GPLv2**
(OpenVPN, ics-openvpn). Proyek `src/RemoteDesktop.Maui` yang memakainya berlisensi GPLv2.

| Komponen | Repo | Ref |
|---|---|---|
| ics-openvpn | https://github.com/schwabe/ics-openvpn | tag `v0.7.69`, commit `f669267e2547fddca04d0bc699739137085eb633` |
| openvpn (submodule `main/src/main/cpp/openvpn`) | https://github.com/schwabe/openvpn | `c29ee42e48df0de5993f7b256829196e67e76006` |
| openssl (submodule) | https://github.com/schwabe/platform_external_openssl | `b280e0c0c81ce6f8d5291c1f8efb6712c357c28a` |
| lz4 (submodule) | https://github.com/lz4/lz4 | `5ff839680134437dbf4678f3d0c7b371d84f4964` |
| lzo | dalam ics-openvpn (`main/src/main/cpp/lzo`) | idem tag ics |

Source tarball persis: https://github.com/schwabe/ics-openvpn/archive/refs/tags/v0.7.69.tar.gz
(plus submodule di atas). Toolchain: Android NDK r27c, `ANDROID_PLATFORM=android-24`, CMake Release.

Modifikasi build (bukan kode): `add_library(openvpn SHARED` diubah ke `STATIC` agar satu file;
OpenVPN3/SWIG dilewati via jalur output "skeleton"; biner di-strip. Hasil: PIE ELF aarch64,
hanya butuh `libc/libm/libdl`, ±5,6 MB (OpenSSL statis).
