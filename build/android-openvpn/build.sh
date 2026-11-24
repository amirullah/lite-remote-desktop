#!/usr/bin/env bash
# Build OpenVPN 2.6 (ics-openvpn fork, TARGET_ANDROID) as a single PIE executable
# libovpnexec.so for arm64-v8a. GPLv2 - see SOURCES.md.
set -euo pipefail
ICS_TAG="${ICS_TAG:-v0.7.69}"
ABI="${ABI:-arm64-v8a}"
API="${API:-24}"
NDK="${ANDROID_NDK_HOME:?ANDROID_NDK_HOME not set}"
WORK="${WORK:-$PWD/work}"
OUT="${OUT:-$PWD/out}"
mkdir -p "$WORK" "$OUT"
if [ ! -d "$WORK/ics" ]; then
  git clone --depth 1 --branch "$ICS_TAG" https://github.com/schwabe/ics-openvpn "$WORK/ics"
  # only the submodules needed for OpenVPN 2 (not openvpn3/asio)
  git -C "$WORK/ics" submodule update --init --depth 50 \
    main/src/main/cpp/openvpn main/src/main/cpp/openssl main/src/main/cpp/lz4 main/src/main/cpp/lzo main/src/main/cpp/fmt main/src/main/cpp/mbedtls || \
  git -C "$WORK/ics" submodule update --init --recursive
fi
CPP="$WORK/ics/main/src/main/cpp"
git -C "$WORK/ics" rev-parse HEAD > "$OUT/ics-openvpn.commit"
git -C "$CPP/openvpn" rev-parse HEAD > "$OUT/openvpn.commit" || true
git -C "$CPP/openvpn" describe --tags --always --long > "$OUT/openvpn.describe" || true
# Link libopenvpn statically into the executable so a single file is shipped.
sed -i 's/add_library(openvpn SHARED/add_library(openvpn STATIC/' "$CPP/CMakeLists.txt"
BLD="$WORK/build-$ABI"
# The "skeleton" output dir path makes ics-openvpn's CMake skip OpenVPN3/SWIG.
cmake -S "$CPP" -B "$BLD" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI="$ABI" -DANDROID_PLATFORM="android-$API" \
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
  -DCMAKE_LIBRARY_OUTPUT_DIRECTORY="$BLD/build/intermediates/cmake/skeletonRelease/obj/$ABI"
cmake --build "$BLD" --target libovpnexec.so -j"$(nproc)"
BIN="$(find "$BLD" -type f -name libovpnexec.so | head -1)"
cp "$BIN" "$OUT/libovpnexec.so"
"$NDK"/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip --strip-unneeded "$OUT/libovpnexec.so"
file "$OUT/libovpnexec.so"; ls -l "$OUT"
"$NDK"/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-readelf -d "$OUT/libovpnexec.so" | grep NEEDED || true
