# Storage for Android — local vertical slice

Two applications, one private repository:

- `service` — `com.everyonesoftware.storage.service`; the native engine and service-private data live here, in a separate `:engine` process.
- `browser` — `com.everyonesoftware.storage.browser`; Compose UI, document-picker import/export, no repository path or native engine.
- `client` — shared AIDL (`IStorage`) and explicit lifecycle-aware binding helper.
- `denied` — **test fixture only**, not a product APK. A same-signer package with the signature permission but no application grant; its instrumentation must fail to read diagnostics.

## Build

Prerequisites: JDK 21, Android platform/build tools 36, NDK `29.0.14206865`; Gradle wrapper 8.13, AGP 8.13.2, Kotlin 2.2.21, Java 17 bytecode. Local environment:

```sh
source /opt/data/tools/android-env.sh
cd /opt/data/code/storage
# Native build is owned by the repository/native worker; never run it concurrently.
bash scripts/build-native.sh
cd apps/android
bash gradlew :service:testDebugUnitTest :service:lintDebug :service:assembleDebug :service:assembleDebugAndroidTest \
  :browser:testDebugUnitTest :browser:lintDebug :browser:assembleDebug :browser:assembleDebugAndroidTest \
  :denied:assembleDebug :denied:assembleDebugAndroidTest --console=plain --max-workers=2
```

Gradle copies the prebuilt Rust core artifacts; it does **not** invoke Cargo. It also compiles a tiny service-only descriptor-flags JNI shim with the pinned NDK:

```
artifacts/native/android/x86_64/libstorage_core.so
artifacts/native/android/arm64-v8a/libstorage_core.so
```

`:service:verifyNativeLibs` fails when either artifact is absent. `-PallowMissingNative=true` is available **only for compile/lint checks while another worker builds native code**. An APK made with missing native code is not runnable or a deliverable; rebuild normally before device installation.

Artifacts under this directory:

```
service/build/outputs/apk/debug/service-debug.apk
browser/build/outputs/apk/debug/browser-debug.apk
service/build/outputs/apk/androidTest/debug/service-debug-androidTest.apk
browser/build/outputs/apk/androidTest/debug/browser-debug-androidTest.apk
denied/build/outputs/apk/debug/denied-debug.apk
denied/build/outputs/apk/androidTest/debug/denied-debug-androidTest.apk
```

Install Service before Browser. Both product APKs must share their signing certificate. Gradle's debug certificate is development-only; production distribution needs a managed signing key. No production fixture data is seeded.

## Release builds

The two product modules apply shared `release.gradle`. Pass the committed-history
count derived by `node scripts/release.mjs version` as `-PstorageCommitCount=N`:
`versionName=0.1.N`, `versionCode=N`, with `N` in `1..65534`. Local debug builds
without the property retain `0.1.0` / code `1`; no Git history is needed for them.
Release tasks require the explicit count, reject `allowMissingNative`, and never
fall back to the debug signing identity. Existing package IDs and signature
permission remain unchanged.

Provision a Storage-owned release key separately. CI uses secrets
`STORAGE_KEYSTORE_BASE64`, `STORAGE_KEYSTORE_PASSWORD`, `STORAGE_KEY_ALIAS`, and
`STORAGE_KEY_PASSWORD`. The workflow decodes the first secret into a private
runner-temporary file and supplies its path as `STORAGE_KEYSTORE_PATH`; the other
three values are environment variables with their secret names. Do not put key
material or passwords in Gradle properties, command-line arguments, repository
files or logs. Missing/partial values and a missing keystore fail closed. Storage
has no configured production signing secrets; a disposable validation key is not
a production identity and must never be published.

From the repository root, after setting up JDK/SDK/NDK/Rust and the signing
environment:

```sh
# Only Android engines, no LLVM-MinGW dependency; honors the pinned Rust toolchain.
bash scripts/build-android-native.sh
bash apps/android/gradlew -p apps/android :service:assembleRelease :browser:assembleRelease \
  -PstorageCommitCount="$COMMIT_COUNT" --no-daemon --console=plain --max-workers=2
node scripts/package-android.mjs --version "$RELEASE_VERSION" --directory dist/android
node scripts/release.mjs manifest --platform android --directory dist/android
```

The Android-only helper accepts `ANDROID_NDK_HOME`, or resolves the pinned NDK
under `ANDROID_HOME` (also accepts `ANDROID_SDK_ROOT`). Output is
`artifacts/native/android/{arm64-v8a,x86_64}/libstorage_core.so`. The existing
all-target `scripts/build-native.sh` remains the local multi-platform option.

Packaging requires the SDK's real `apksigner` and `apkanalyzer`. It verifies both
signatures, rejects Android debug certificates and differing signers, checks exact
application IDs and embedded versions, and requires both core/descriptor ABIs in
Service and neither service-private library in Browser. It validates the complete
pair before copying `Storage-Service-0.1.N.apk` and `Storage-Browser-0.1.N.apk`;
parent release tooling owns checksums and the manifest.

`node --test tests/android-release.test.mjs` runs SDK-independent mocked-boundary
checks. With an installed SDK/JDK, set `STORAGE_ANDROID_GRADLE_TESTS=1` to also run
actual Gradle fail-closed configuration checks. Real signed/debug APK builds and
real signature verification are separately recorded in `VALIDATION.md`.

## Device verification

Use only an explicitly owned emulator; never adopt another task's device, start an emulator implicitly, or kill the shared adb server. The parent owns `emulator-5592` for this task; the Android worker does not operate it without delegation.

After a **normal native-containing build**, the emulator owner can run:

```sh
source /opt/data/tools/android-env.sh
cd /opt/data/code/storage/apps/android
bash validate-device.sh emulator-5592
```

This script installs six APKs, tests actual cross-process Binder import/list/read/version retention, same-signer ungranted caller denial, cold process-death persistence (seed → force-stop service → verify), and the actual Compose Browser against the actual service and native engine. It rejects missing/wrong JUnit totals, negative test status codes, skipped tests and instrumentation crashes, rather than relying on adb's exit status.

Expected passing counts: service Binder 8, denied caller 1, cold seed 1, cold verify 1, same-client cold binding restart 1, listing notice UI 2, Browser 2. `build/device-validation/` receives stdout logs plus real `items-grid.png` and `item-details.png` screenshots. Binding-death callbacks are injected only for deterministic lifecycle cases; the cold-binding test separately force-stops the actual service process, requires the old Binder to fail a ping, reconnects the same client, and checks stable identity/exact bytes. The Browser test creates one uniquely named fixture file through Binder; it never inserts directly into service storage. Fixture items remain on the task-owned emulator for inspection. A cold test must be explicitly selected with phase `seed` or `verify`; do not run the entire service test package without a phase.

The task API-29 image reports night mode locked off: `cmd uimode night yes` still returns `no`. Existing screenshots from that image are light-theme evidence only, regardless of filenames. Dark-theme runtime evidence needs a device/image where the actual night configuration changes. Manually exercise document-picker import, current/historical export, export-and-open, grid/list toggle, collections and navigation; test APIs do not replace these platform-picker acceptance gates.

## Security and limits

- Android enforces a signature binding permission. Each Binder method additionally resolves the authenticated UID to its packages, verifies the signing certificate, and requires the exact first-party Browser package grant. The service's own UID is permitted for its internal instrumentation. Package name, caller JSON and a shared certificate alone are insufficient.
- The service constructs `local-owner` and its explicit capability list. No caller can submit `actor`, `repository`, `sourcePath` or a raw native command. Query names and exact field sets are allowlisted, type-checked and reconstructed; reads use a dedicated descriptor method.
- Import/new-version bytes cross IPC as file descriptors. At most two Binder threads stage imports concurrently; additional imports fail promptly rather than queue. Metadata/hash reads have a separate two-worker executor with a bounded 16-entry queue and 30-second waits. Staging has a 10-second idle deadline, a 60-second overall deadline and 1 GiB limit. Sources are nonblocking and polled in at most 250 ms increments; reliable producer cancellation is checked before publication. The service closes owned descriptors on every exit and on destruction, and removes staging files. A tiny service-only NDK shim sets `O_NONBLOCK` on API 26–29, which lack a public Java fcntl setter; Gradle compiles it for both supported ABIs without rebuilding Rust. Callers perform synchronous Binder calls on IO dispatchers, not the UI thread. No base64/large JSON content transport.
- Read authorization and hash verification happen in the core before the service returns a read-only descriptor. No internal content path is returned in public JSON. Core error messages are replaced with safe platform messages.
- Argument JSON is bounded at 16 KiB; response JSON at 512 KiB. This local slice has bounded listings, not a production paging API. The Browser exposes actual `hasMore` flags for items, collection contents, collections and versions instead of treating partial results as complete; name search applies only to loaded items. Durable upload jobs/progress and quota scheduling remain future work.
- Browser exports by copying the service descriptor into the user's chosen destination. “Export and open” opens that destination, not a repository path or a browser-hosted content engine.
- Backup and device-transfer extraction are disabled; Android application isolation remains the boundary. No network permission is requested.

## UI scope

Items have real loading/empty/error states, name search, adaptive grid/list, file import, attributes, immutable history, new-version import, current/historical export and export-and-open. Collections can be created, browsed and populated with references to existing items. Diagnostics show actual service health/counts/stable IDs. Light/dark follow the system.

Saved Queries, pairing and replication are visibly not implemented. Settings has an explicit update-check action that reports the missing release feed, never a fabricated “up to date” result. Collection removal/deletion, rename and access-policy mutation exist in the service allowlist but are not exposed in this initial UI. This is not full first-release acceptance.

See `VALIDATION.md` for actual evidence and remaining gates.
