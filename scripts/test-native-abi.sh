#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
LIB="$ROOT/artifacts/native/linux-x64/libstorage_core.so"
[[ -f "$LIB" ]] || { printf 'Build the native core first: bash scripts/build-native.sh\n' >&2; exit 1; }
mkdir -p artifacts/conformance
cc -Wall -Wextra -Werror -o artifacts/conformance/abi-driver tests/conformance/abi-driver.c -L"$(dirname "$LIB")" -lstorage_core -Wl,-rpath,"$(dirname "$LIB")"
node --test tests/conformance/native-abi.test.mjs
