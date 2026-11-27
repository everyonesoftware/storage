# Storage for Android — local vertical slice

Two applications, one private repository:

- `service` — `com.everyonesoftware.storage.service`; the native engine and service-private data live here, in a separate `:engine` process.
- `browser` — `com.everyonesoftware.storage.browser`; Compose UI, document-picker import/export, no repository path or native engine.
- `client` — shared AIDL (`IStorage`) and explicit lifecycle-aware binding helper.
- `denied` — **test fixture only**, not a product APK. A same-signer package with the signature permission but no application grant; its instrumentation must fail to read diagnostics.

## Product icon inventory

- Browser's canonical `browser/src/main/res/drawable/ic_storage.xml` remains the
  approved folder, unchanged. Service's corresponding canonical vector is the
  approved two-bay engine. Both use the 48-unit viewport, teal `#006B64` background
  and `#E8FFFC` foreground. The engine's fixed dots and slots are decorative,
  **not** live health, availability, or connectivity indicators.
- Both application manifests use `@mipmap/ic_launcher` for `icon` and `roundIcon`.
  API-26 adaptive resources use a full-bleed teal background and centered,
  transparent 108-unit foreground. API-33 resources additionally reference a
  single-color glyph with transparent detail cutouts for themed icons. The same
  glyph geometry is preserved inside the central adaptive safe area. Browser has
  its existing launcher activity. The Storage Service launcher opens only the
  device pairing screen and uses the engine icon.
- Browser's root top bar and shared Items/search/Queries empty-state illustration
  use the untinted canonical folder. Detail/collection navigation retains Back.
  Settings intentionally has no product header/About panel. Its updater uses text
  for the two component names, not an embedded product icon. Functional navigation/action/file icons are not product
  branding and remain unchanged.
- Neither product currently posts notifications or defines a notification small
  icon (`Notification.Builder`, `NotificationCompat`, `setSmallIcon`, and
  `startForeground` have no product-source call sites). No notification channel,
  foreground-service lifecycle, or permission is added for branding. Future small
  icons must be transparent, single-color silhouettes, never the opaque full-color
  application tile or adaptive launcher drawable.
- The `denied` APK is a non-product authorization fixture; it remains unbranded.

Regression checks: `node --test tests/android-icons.test.mjs`. The existing
`tests/android-release.test.mjs` imports them so ordinary CI's explicit Android
release-test command also runs the icon guards; no CI/release workflow is changed.

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
  -PstorageCommitCount="$COMMIT_COUNT" -PstorageSourceCommit="$SOURCE_COMMIT" --no-daemon --console=plain --max-workers=2
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

Use only an explicitly owned emulator; never adopt another task's device, start an emulator implicitly, or kill the shared adb server. The updater validation used its own `storage-updates-task29` AVD at `emulator-5586`; the earlier `emulator-5592` target in historical records is not implicitly available to other tasks.

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
- Backup and device-transfer extraction are disabled; Android application isolation remains the boundary. Browser uses network access for the public release updater. Service uses it for foreground, user-initiated pairing on the local network and requests camera access only when scanning.

## UI scope

Items have real loading/empty/error states, name search, adaptive grid/list, file import, attributes, immutable history, new-version import, current/historical export and export-and-open. Collections can be created, browsed and populated with references to existing items. Diagnostics show actual service health/counts/stable IDs. Light/dark follow the system.

Saved Queries and replication are not implemented. Settings checks the public GitHub release inventory for the Android publication, independently of Windows; see the update flow below. Collection removal/deletion, rename and access-policy mutation exist in the service allowlist but are not exposed in this initial UI. This is not full first-release acceptance.

## Pair with a Windows PC

Pairing requires Android 10 or later. On Windows, open **Storage Service → Devices → Add Android phone**, select the address on your local network, and show the pairing QR code. On Android, launch **Storage Service** (not Storage Browser) and choose **Scan QR code**. To join without a camera, choose **Add device** on Windows instead, and enter the PC's hostname or IPv4 address in the Android app.

Compare all six digits on both screens. Confirm the match on Android, then approve the device on Windows. This saves the paired coordinator and does not copy files or extend Browser permissions. See the [device pairing contract](../../contracts/device-pairing.md).

## Application updates

Settings shows the real installed Browser version in the compact **Current Version**
heading. PackageManager's Browser and Service identities are independently checked
for update eligibility; the retired Package versions section is not displayed.
The Settings-only application header/refresh action, Updates heading, About Storage
and Appearance sections are omitted; the five-tab navigation is retained.

**What’s new** loads the complete installed-to-offered source range on demand,
grouped into Improvements and Fixes with original subjects/hashes in Commit details.
Complete per-commit file metadata determines platform relevance; mixed/shared changes
are retained. Missing source identity, truncated history and network errors remain
unavailable/retry states without blocking a valid download. `storageSourceCommit`
embeds source identity only for matching clean committed release inputs; debug builds
deliberately retain unknown source identity. CI supplies the same SHA as the release
manifest. This is not a release-description or version-tag-based changelog.

User flow: **Check now → Download update** (combined byte progress) → allow
installs from Storage in Android Settings if necessary → **Install Service update**
→ **Continue installation…** → Android's Service confirmation → return to Settings
→ **Install Browser update** → **Continue installation…** → Android's Browser confirmation. No permission or install
prompt is automatically approved. Check/download/install actions are serialized;
cancellation and retries keep both installed packages and Service data intact.

The credential-free client enumerates at most 20 pages of 100 public GitHub releases,
ignores drafts/prereleases and releases without `storage-android-release.json`, and
validates the highest numeric Android publication's complete three-file inventory.
A corrupt present marker, failed request or incomplete pagination is an error, never
an “up to date” result. Both versioned APKs are required. HTTPS origins/redirects,
metadata sizes, download sizes/deadlines, SHA-256, exact package IDs/versions and
current signing certificates are checked before OS-owned sessions are prepared.
Installed versions may not be lowered. The installed Service/Browser signer must
match, and each APK must match its installed product; signing-key rotation is not
silently accepted. Android is the final cryptographic/install-policy authority.

All supported Android versions (API 26+) use one public, user-confirmed package
session at a time: **Service first, then Browser**. Browser is not eligible until
PackageManager observes Service at the selected version with the verified installed
signing identity. Returning from Service installation refreshes both real versions
and exposes a separate Browser action; it never automatically installs Browser.
Expect two OS confirmations when both products need updating. Equal-version
components are verified but not reinstalled. Download metadata and OS session IDs
are private durable state; Activity recreation does not cancel process-owned work.
After process death, incomplete files are not trusted: retry downloads both from
the start, and cached complete pairs are revalidated before use. Pending Android
confirmation and installer results survive controller recreation. No automatic
startup install, repository read, uninstall, data clear, credential or service grant
change is part of updating.

Older published builds with placeholder Settings need **one manual in-place upgrade**
to an updater-enabled release, using both APKs signed with their existing identity.
Debug APKs cannot update to a differently signed release; never uninstall Service
or clear its data as a workaround. This implementation is not itself publication.

### Updater instrumentation fixtures

Before running `validate-device.sh`, generate the explicit **test-only debug APKs**:

```sh
source /opt/data/tools/storage-env.sh
bash apps/android/build-update-fixtures.sh
bash apps/android/validate-device.sh <task-owned-serial>
```

The helper builds code-2 debug Service/Browser APKs into ignored
`browser/build/update-fixtures/updates/`, embeds those files only in the instrumentation
APK, then restores normal code-1 product outputs. It never builds/releases with a
production key, installs packages, or approves OS prompts. Do not run Gradle clean
between fixture generation and instrumentation packaging. New exact expected counts:
updater installer **4**, updater Settings **4**, updater restart/real commit **5**,
Browser-UID continuity seed **1** and cold verify **1**, plus the existing **16** tests
(**31** total). The phased continuity test imports and reads a test-only 65,539-byte
fixture through real Binder, persisting the actual store/device/version IDs for
verification after each separately OS-approved installation.
The Settings permission test opens the real per-source Android Settings page and
returns without granting it; real install acceptance remains a separately user-confirmed
device gate. See `VALIDATION.md` for the observed pre-existing Service-test flake,
the final full passing gate, and separate updater/runtime results.

Official API references checked for this implementation: Android
`PackageInstaller.SessionParams` (standalone, API 26+),
`SessionParams.setRequireUserAction` (API 31), `Session.commit`,
`STATUS_PENDING_USER_ACTION`, `PackageManager.canRequestPackageInstalls`, and
`Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES`. The API-29 archive parser also needs
`GET_SIGNATURES` alongside `GET_SIGNING_CERTIFICATES` to collect certificates;
verification still compares current `apkContentsSigners`, not signing history.

### Sequential native acceptance

The final source was rebuilt into code-2 debug fixtures, then into normal code-1
products. `build/update-validation/sequential/final2-full-build.log` records the
full product/JVM/lint build; `final2-device-gate.log` passed all 31 exact-count tests
on the task-owned API-29 emulator. Browser JVM: 23 tests; Service JVM: 5 tests,
with no failures/errors/skips. Node updater guards: 8 passing. Red evidence includes
`policy-red.log`, `identities-red.log`, `equal-version-red.log` and source/count
regressions; the original real atomic OS-commit failure is retained separately in
`../review-final-atomic-commit-red.log` relative to this evidence directory.

`build/update-validation/sequential/os-final/` retains inspected screenshots, real
OS journals/package dumps, installed-APK hashes, post-upgrade cold/Binder logs and
`verified-result.json`. Actual Settings and OS UI upgraded **Service 1→2**, then
**Browser 1→2**, with a real Service decline/retry and Browser process death while
Service consent was pending. After each successful step, Service cold verification
and Browser-UID reads returned the original exact 65,539 bytes with unchanged real
store/device IDs. Pulled installed APK hashes exactly match the final code-2 fixtures.
No install/download action is offered once both installed products match.

This is a **debug-fixture emulator acceptance**, not publication or public-download
acceptance: fixture bytes and synthetic release metadata were staged in Browser's
private directory using `run-as`; production archive/hash/signer validation and the
actual Settings controller/session path were then used. Source permission and OS
install consent were explicitly clicked in the real UI for these same-debug-signer
fixtures. Automated pending-consent tests temporarily set the owned source app-op
only to exercise real commit/recovery; they never approve installation and are not
upgrade proof. A pre-publication failed atomic attempt's journal was preserved in
`legacy-atomic-journal.xml` and renamed on this test AVD to isolate the sequential
tests; its stranded OS children were not silently swept. No Service data was cleared.
An intermediate successful run was restored to code-1 via debug-only `adb install
-r -d` solely as test setup for the final-source rerun; the final recorded upgrades
were performed through the actual OS UI, not adb replacement.

API-26, API-31+ and physical-phone runtime acceptance remain separate unverified
gates. The policy JVM tests cover API 26/28/29/31/36, not native execution on those
other OS versions. No production key, publication, repository migration, frozen
Binder protocol change, uninstall or automatic Browser installation was involved.
See `VALIDATION.md` for the parent-owned validation summary and remaining gates.
