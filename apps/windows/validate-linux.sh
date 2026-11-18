#!/usr/bin/env bash
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
dotnet="${DOTNET:-/opt/data/tools/dotnet/dotnet}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-1}"
export TMPDIR="${TMPDIR:-/opt/data/cache/scratch}"
mkdir -p "$TMPDIR" "$here/validation"
native="${1:-$root/artifacts/native/linux-x64/libstorage_core.so}"
if [[ ! -f "$native" ]]; then printf 'Missing real native core: %s\n' "$native" >&2; exit 2; fi
"$dotnet" run --project "$here/Storage.Tests" -- "$native" | tee "$here/validation/logic-transport.txt"
"$dotnet" build "$here/Storage.Service/Storage.Service.csproj" -r win-x64 --self-contained true | tee "$here/validation/service-compile.txt"
"$dotnet" build "$here/Storage.Browser/Storage.Browser.csproj" -p:OutputType=Library -p:WindowsAppSDKSelfContained=false -p:SelfContained=false -p:PrepareForRunDependsOn=CopyFilesToOutputDirectory | tee "$here/validation/winui-compile-only.txt"
node "$here/validation/inspect-artifacts.mjs" "$native" | tee "$here/validation/artifact-check.txt"
printf 'Compile and Linux transport/native integration only. Native Windows runtime and PRI deployment still require Windows.\n'
