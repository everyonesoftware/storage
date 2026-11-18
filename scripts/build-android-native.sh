#!/usr/bin/env bash
# Android-only CI build: deliberately independent of LLVM-MinGW and host builds.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
NDK_VERSION=29.0.14206865
NDK="${ANDROID_NDK_HOME:-${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}/ndk/$NDK_VERSION}"
[[ -f "$NDK/source.properties" ]] && grep -Eq "^Pkg\\.Revision[[:space:]]*=[[:space:]]*${NDK_VERSION//./\\.}[[:space:]]*$" "$NDK/source.properties" || { printf 'Android NDK %s required; set ANDROID_NDK_HOME or ANDROID_HOME\n' "$NDK_VERSION" >&2; exit 1; }
case "$(uname -s)" in
  Linux) HOST=linux-x86_64 ;;
  Darwin) HOST=darwin-x86_64 ;;
  *) printf 'Unsupported Android native build host (use Linux or macOS)\n' >&2; exit 1 ;;
esac
NDK_BIN="$NDK/toolchains/llvm/prebuilt/$HOST/bin"
for tool in cargo "$NDK_BIN/aarch64-linux-android26-clang" "$NDK_BIN/x86_64-linux-android26-clang" "$NDK_BIN/llvm-ar"; do
  command -v "$tool" >/dev/null || { printf 'Missing Android build tool: %s\n' "$tool" >&2; exit 1; }
done
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$ROOT/artifacts/cargo}"
OUTPUT="${STORAGE_ANDROID_NATIVE_OUTPUT:-$ROOT/artifacts/native/android}"
export CC_aarch64_linux_android="$NDK_BIN/aarch64-linux-android26-clang"
export AR_aarch64_linux_android="$NDK_BIN/llvm-ar"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER="$CC_aarch64_linux_android"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS='-C link-arg=-Wl,-z,max-page-size=16384'
cargo build --locked -p storage-core --release --target aarch64-linux-android
export CC_x86_64_linux_android="$NDK_BIN/x86_64-linux-android26-clang"
export AR_x86_64_linux_android="$NDK_BIN/llvm-ar"
export CARGO_TARGET_X86_64_LINUX_ANDROID_LINKER="$CC_x86_64_linux_android"
export CARGO_TARGET_X86_64_LINUX_ANDROID_RUSTFLAGS='-C link-arg=-Wl,-z,max-page-size=16384'
cargo build --locked -p storage-core --release --target x86_64-linux-android
# Copy only after both builds succeed.
mkdir -p "$OUTPUT/arm64-v8a" "$OUTPUT/x86_64"
cp "$CARGO_TARGET_DIR/aarch64-linux-android/release/libstorage_core.so" "$OUTPUT/arm64-v8a/"
cp "$CARGO_TARGET_DIR/x86_64-linux-android/release/libstorage_core.so" "$OUTPUT/x86_64/"
printf 'Built Android engines: %s/{arm64-v8a,x86_64}/libstorage_core.so\n' "$OUTPUT"
