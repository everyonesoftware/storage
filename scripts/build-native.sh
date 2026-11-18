#!/usr/bin/env bash
# Build the same service-owned engine for the host, Windows and Android.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ -f /opt/data/tools/storage-env.sh ]]; then
  source /opt/data/tools/storage-env.sh
fi
: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to the pinned Android NDK}"
: "${LLVM_MINGW_HOME:?Set LLVM_MINGW_HOME to the LLVM-MinGW toolchain}"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$ROOT/artifacts/cargo}"
NDK_BIN="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin"
MINGW_BIN="$LLVM_MINGW_HOME/bin"
for tool in cargo "$NDK_BIN/aarch64-linux-android26-clang" "$NDK_BIN/x86_64-linux-android26-clang" "$MINGW_BIN/x86_64-w64-mingw32-clang"; do
  command -v "$tool" >/dev/null || { printf 'Missing tool: %s\n' "$tool" >&2; exit 1; }
done

cargo build --locked -p storage-core --release
mkdir -p artifacts/native/linux-x64
cp "$CARGO_TARGET_DIR/release/libstorage_core.so" artifacts/native/linux-x64/

export CC_aarch64_linux_android="$NDK_BIN/aarch64-linux-android26-clang"
export AR_aarch64_linux_android="$NDK_BIN/llvm-ar"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER="$CC_aarch64_linux_android"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS='-C link-arg=-Wl,-z,max-page-size=16384'
cargo build --locked -p storage-core --release --target aarch64-linux-android
mkdir -p artifacts/native/android/arm64-v8a
cp "$CARGO_TARGET_DIR/aarch64-linux-android/release/libstorage_core.so" artifacts/native/android/arm64-v8a/

export CC_x86_64_linux_android="$NDK_BIN/x86_64-linux-android26-clang"
export AR_x86_64_linux_android="$NDK_BIN/llvm-ar"
export CARGO_TARGET_X86_64_LINUX_ANDROID_LINKER="$CC_x86_64_linux_android"
export CARGO_TARGET_X86_64_LINUX_ANDROID_RUSTFLAGS='-C link-arg=-Wl,-z,max-page-size=16384'
cargo build --locked -p storage-core --release --target x86_64-linux-android
mkdir -p artifacts/native/android/x86_64
cp "$CARGO_TARGET_DIR/x86_64-linux-android/release/libstorage_core.so" artifacts/native/android/x86_64/

export CC_x86_64_pc_windows_gnullvm="$MINGW_BIN/x86_64-w64-mingw32-clang"
export AR_x86_64_pc_windows_gnullvm="$MINGW_BIN/llvm-ar"
export CARGO_TARGET_X86_64_PC_WINDOWS_GNULLVM_LINKER="$CC_x86_64_pc_windows_gnullvm"
export CARGO_TARGET_X86_64_PC_WINDOWS_GNULLVM_RUSTFLAGS='-C target-feature=+crt-static'
cargo build --locked -p storage-core --release --target x86_64-pc-windows-gnullvm
mkdir -p artifacts/native/windows-x64
cp "$CARGO_TARGET_DIR/x86_64-pc-windows-gnullvm/release/storage_core.dll" artifacts/native/windows-x64/

printf '\nBuilt native engines:\n'
sha256sum artifacts/native/linux-x64/libstorage_core.so artifacts/native/android/arm64-v8a/libstorage_core.so artifacts/native/android/x86_64/libstorage_core.so artifacts/native/windows-x64/storage_core.dll
