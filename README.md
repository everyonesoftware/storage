# Storage

<!-- current-downloads:start -->
## Current downloads

Choose your platform. Each platform keeps its newest verified manifest-backed publication; versions may differ.

| Platform | Version | Download | Status |
| --- | --- | --- | --- |
| Windows x64 | 0.1.30 | [Installer (.exe)](https://github.com/everyonesoftware/storage/releases/download/v0.1.30/StorageWindows-0.1.30-win-x64-Setup.exe) · [SHA-256](https://github.com/everyonesoftware/storage/releases/download/v0.1.30/StorageWindows-0.1.30-win-x64-Setup.exe.sha256) | Unsigned; Windows may show an unknown-publisher/SmartScreen warning. |
| Android | 0.1.32 | [Service APK](https://github.com/everyonesoftware/storage/releases/download/v0.1.32/Storage-Service-0.1.32.apk) · [Browser APK](https://github.com/everyonesoftware/storage/releases/download/v0.1.32/Storage-Browser-0.1.32.apk) · [SHA-256](https://github.com/everyonesoftware/storage/releases/download/v0.1.32/Storage-android-SHA256SUMS) | Install Service first, then Browser. Signed with the Storage release key. |

Verify SHA-256 checksums before installation. Unchanged platforms retain their original release, version and source identity. These downloads are not an in-app update feed.
<!-- current-downloads:end -->

The tables refresh automatically after publication. See [current download discovery](docs/CURRENT-DOWNLOADS.md) for preview commands and refresh behavior.

A native Windows/Android storage platform in development. Applications address logical Items, immutable ItemVersions and Collections through a device service—not filesystem repositories or other devices.

## Current scope

The initial local vertical slice contains:

- One shared Rust engine with transactional SQLite metadata, stable device/Data Store identities, streamed SHA-256 content ingestion, immutable versions, optimistic revisions and independent AccessPolicies.
- Separate native Windows service, logical C# client and WinUI Browser.
- Separate Android service and Compose Browser APKs with authenticated Binder IPC and a Kotlin client.
- Local import, browsing, generated Attributes, version history, retrieval and reference-based Collections.
- Tests of actual on-disk content, native ABI, service transport, denied callers and process restart.

This is **not yet the distributed first release**. Pairing, cross-device metadata, replication, placement planning, pinning and saved Queries are not implemented. User-initiated in-app updating is implemented in the working tree for both native Browsers; see [application updates](contracts/application-updates.md) for the flow and acceptance boundaries. Existing placeholder builds need one manual in-place upgrade to the first updater-enabled release. Linux compilation is not Windows runtime verification, and Android emulator tests are not physical-phone acceptance.

The Windows service denies unapproved clients by default. Installed first-party Browser consent and explicitly hash-pinned development enrollment are documented in the Windows README. Android product APKs use the development signing certificate in debug builds; published product APKs use the separate Storage release key. No test Items are seeded into production startup.

## Architecture and contracts

- [Approved architecture](docs/architecture-proposal.md)
- [Initial integration surface](contracts/first-slice.md)
- [Threat model and boundaries](docs/threat-model.md)
- [Original first-release acceptance checklist](docs/acceptance.md)
- [Integrated validation and current limits](docs/validation.md)
- [CI, installers and release activation](docs/RELEASING.md)
- [Installed toolchain evidence](docs/toolchains.md)

Browser processes never access Data Store internals or choose physical placement. The internal core ABI accepts service-private paths and a trusted actor; it must never be exposed as an application command tunnel.

## Build and validate

In the current development environment:

```sh
source /opt/data/tools/storage-env.sh
cargo test --workspace --locked
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
bash scripts/build-native.sh
bash scripts/test-native-abi.sh
bash apps/windows/validate-linux.sh "$PWD/artifacts/native/linux-x64/libstorage_core.so"
```

The native script emits the same engine as a Linux shared object, Windows x64 DLL, Android arm64 library and Android x86_64 library under `artifacts/native/`. Toolchain versions are pinned; installing equivalents elsewhere requires configuring `CARGO_HOME`, `RUSTUP_HOME`, `ANDROID_NDK_HOME` and `LLVM_MINGW_HOME` appropriately. No Python tooling is required.

### Android

```sh
cd apps/android
bash gradlew :service:testDebugUnitTest :service:lintDebug :service:assembleDebug :service:assembleDebugAndroidTest \
  :browser:testDebugUnitTest :browser:lintDebug :browser:assembleDebug :browser:assembleDebugAndroidTest \
  :denied:assembleDebug :denied:assembleDebugAndroidTest --console=plain --max-workers=2
# Only on a device/emulator explicitly owned for this task:
bash validate-device.sh <owned-serial>
```

Install `service/build/outputs/apk/debug/service-debug.apk` before `browser/build/outputs/apk/debug/browser-debug.apk`. Both must have the same signing identity. The `denied` APK and all androidTest APKs are test fixtures, not product deliverables. Never deliver an APK assembled using the compile-only missing-native escape hatch.

See [Android instructions](apps/android/README.md) and its [validation record](apps/android/VALIDATION.md).

### Windows

On native Windows with .NET 10 and the Windows build tools/SDK, using the prebuilt Rust DLL:

```powershell
./apps/windows/build.ps1
# Explicit development-only enrollment; read the documented security boundary first.
./apps/windows/start-development.ps1 -EnrollDevelopmentBrowser
```

See [Windows instructions](apps/windows/README.md). Native PRI generation, DLL loading, pickers/drop, titlebar behavior and installation are required Windows gates, not claims made by the Linux compile script.

## Development conventions

Read [AGENTS.md](AGENTS.md). Use this shared checkout, preserve uncommitted work, and do not commit or push without authorization. Every reported runtime result must come from the actual implementation. Platform build outputs remain ignored; source control is not a binary release feed.
