# Android validation evidence

## Release automation build-only validation — November 18, 2026

- Added shared product `release.gradle` version/signing rules, Android-only native
  build helper and actual-APK release packager. No package IDs, signature
  permission, separate-process/service boundary or application code changed.
- RED: the Node behavior tests initially failed because the portable helper and
  packager were absent, and a real Gradle release dry-run incorrectly succeeded
  without signing. GREEN: `STORAGE_ANDROID_GRADLE_TESTS=1 node --test
  tests/android-release.test.mjs` passed **4 tests**, including a matrix of missing,
  partial and nonexistent signing configuration, absent count, malformed/out-of-
  range counts and forbidden missing-native release builds. A later NDK literal-
  version regression was observed RED and fixed GREEN.
- Without `ANDROID_HOME`, `ANDROID_NDK_HOME`, `ANDROID_SDK_ROOT` or `JAVA_HOME`, the
  default Node test command passed its **2 SDK-independent tests**; the **2 actual
  Gradle tests are explicitly opt-in/skipped**. The fake command/metadata fixtures
  are tests only, never release evidence.
- Real `bash scripts/build-android-native.sh` built both Rust Android engines with
  pinned NDK `29.0.14206865`, without invoking any Windows toolchain. Existing
  `scripts/build-native.sh` was not changed.
- Actual native-containing Service/Browser debug builds, both debug lint tasks and
  JVM test tasks succeeded. A separate `--rerun-tasks` JVM run also succeeded:
  **5 service JVM tests, 0 failures/errors/skips**; Browser remains `NO-SOURCE`.
  Lint: Service **0 errors, 4 warnings**; Browser **0 errors, 7 warnings**.
  Logs: `/opt/data/cache/scratch/storage-android-debug-regression.log` and
  `/opt/data/cache/scratch/storage-android-release-jvm-regression.log`.
- Generated a **disposable test-only keystore outside the checkout**, never a
  production signing identity. Both real release builds succeeded with explicit
  test count **7**. The actual SDK `apksigner`/`apkanalyzer` verifier accepted the
  matching non-debug test signer, embedded `0.1.7` / code `7`, exact package IDs,
  both Service core/descriptor ABIs and absence of those libraries in Browser.
  The CLI copied the exact required names under the clearly marked scratch
  directory `/opt/data/cache/scratch/storage-android-TEST-ONLY-LlR7s4/apks/`:
  `Storage-Service-0.1.7.apk` (**5,497,400 bytes**) and
  `Storage-Browser-0.1.7.apk` (**12,255,237 bytes**). These are **validation fixtures,
  not production deliverables**. Release build log is in the same scratch parent.
- Actual APK negative checks rejected debug-signed APKs and a wrong requested
  embedded version before creating an output directory. Shell and Node syntax
  checks passed.
- No emulator was started, adopted, installed into or stopped. Existing 16-pass
  device evidence below was not rerun for this build-only configuration change.
  No GitHub secrets were provisioned and nothing was committed, pushed or
  published. Production publication remains blocked until a separately authorized
  Storage release key is provisioned. The test keystore and test-signed product
  release build outputs were removed after validation to prevent accidental use.


## Independent-review fixes — November 17, 2026

**Current result: full native-containing build, unit/lint and owned-emulator validation passed.** This supersedes the historical source-only handoff below. Exclusive Gradle and Storage APK/test operation ownership is released to the parent.

### Fixes and runtime evidence

- `StorageService` isolates metadata/hash workers from descriptor staging. Admission is bounded to two imports with no import queue; metadata uses two workers and a 16-entry bounded queue. Staging polls nonblocking sources every 250 ms, expires after 10 seconds idle or 60 seconds overall, honors reliable-pipe producer cancellation before publication, and closes descriptors on failure/destruction. Future waits are bounded to 30 seconds and unwrap native read failures into Binder-safe `SecurityException`.
- `StorageConnection` synchronizes lifecycle state, replaces a dead binding and its future, unbinds it exactly once, fails pending waiters, ignores stale-generation callbacks, and permits same-instance reconnect after close/death.
- Browser item/collection/version lists surface actual `hasMore` flags. Partial item counts say “loaded items”; the warning states search covers only loaded results. Paging itself is not implemented.
- Preserved parent fixes: `runStorageOperation` naming, Compose BOM `2026.03.01`, clickable noneditable card selector, and UiAutomation keyboard Back action.

RED evidence under ignored `apps/android/build/`:

- `idle-red.log`: actual two-idle-pipe imports exhausted the old executor; health timed out at the 3-second bound. `idle-green.log`: the same Binder regression passed.
- `reconnect-red.log`: death after connection returned the same completed future. `reconnect-green.log`: all five tests present at that point passed.
- `safety-negative-red.log`: removing descriptor shutdown, reliable-pipe error checking and exception unwrapping yielded three real Binder failures (shutdown timed out; cancelled bytes were published; failed descriptor read became a null result). Restored code passes all final tests.
- `listing-red.log`: a deliberately empty notice renderer failed its visible-warning assertion; final renderer passes both UI cases.

API-29 correction: Java `Os.fcntlInt` is public only from API 30. Reopening another UID's passed descriptor through `/proc/self/fd` was rejected during the actual Browser cold-binding import. The final `service/src/main/c/descriptor_io.c` shim sets `O_NONBLOCK` directly on the received descriptor using the pinned NDK; no hidden Java APIs or path reopening remain. Both supported ABIs are packaged, and cross-UID Browser imports pass. The Browser APK contains neither core nor descriptor-helper libraries.

### Final executed gates

Source `/opt/data/tools/storage-env.sh`, then from `apps/android`:

```sh
bash gradlew :service:testDebugUnitTest :service:lintDebug :service:assembleDebug :service:assembleDebugAndroidTest \
 :browser:testDebugUnitTest :browser:lintDebug :browser:assembleDebug :browser:assembleDebugAndroidTest \
 :denied:assembleDebug :denied:assembleDebugAndroidTest --console=plain --max-workers=2
bash validate-device.sh emulator-5592
```

- Build: `BUILD SUCCESSFUL in 11s`, 265 tasks (23 executed, 242 up-to-date), no missing-native escape hatch. Log: `build/review-validation-build.log`.
- JVM XML parsed: **2 tests, 0 failures/errors/skips**. Browser JVM remains `NO-SOURCE`.
- Lint XML parsed: service **0 errors, 4 warnings**; Browser **0 errors, 7 warnings** (existing dependency/backup suggestions).
- Exact instrumentation totals parsed and rejected on any negative status/skip/failure: **16 passes** — service Binder **8**, denied caller **1**, cold seed **1**, cold verify **1**, same-client real process-death reconnect **1**, listing notice UI **2**, real Browser UI **2**.
- Final logs: `build/device-validation/{service-binder,denied-caller,cold-seed,cold-verify,cold-binding,listing-notice,browser-binder}.log`; combined log `build/review-device-validation.log`.
- Service Binder regressions cover two idle imports with responsive health, promptly rejected third import, idle-timeout errors with unchanged Item count and closed pipe readers, reliable cancellation with unchanged count, destruction cancelling an idle source, pending/connected binding death, stale callbacks, same-instance close/reopen, safe unavailable read, and original byte/version/auth cases.
- The separate Browser cold-binding test force-stops the actual service, verifies its old Binder no longer answers a ping, reconnects the **same** client instance and checks stable datastore identity and exact retained bytes. Injected callbacks are used only for deterministic pending/connected lifecycle cases.
- `bash -n validate-device.sh` passed. Expected totals in that script match source and actual runtime output.

Target: task-owned `storage-initial-api29`, `emulator-5592`, Android API 29, **412×915 at density 160**. The prior emulator exited; two one-core recovery attempts also hit QEMU watchdog hangs. The successful recovery used two software cores, Vulkan disabled and a 412×915 framebuffer. Its tracked process `proc_1d4f0271f15c` was handed to the parent and left running; no shared adb server or other emulator was stopped.

Actual `build/device-validation/items-grid.png` and `item-details.png` were inspected: light theme, fixture card, readable attributes and export/new-version actions, no visible service error. The search field horizontally scrolls its long fixture name, and the details app-bar title ellipsizes normally. API-29 night mode remains locked off; **no dark-theme evidence is claimed**.

Remaining boundaries at worker handoff were closed by the parent where recorded in `docs/validation.md`: the updated core was rebuilt for every target, repackaged, and the full 16-execution Android gate passed again; graphical document-picker import/export also passed a byte-for-byte check. The notice-renderer tests do not independently manufacture a >500-record native listing. Physical phone, dark theme, Windows runtime and replication remain unverified/unimplemented as specified in the integrated record.

## Initial source-only handoff — historical, superseded

**Status: source/build gates passed; JNI packaging and device runtime gates remain with the parent.** Gradle ownership released after the final successful build. This worker did not start, install into, or operate an emulator. The parent's task-owned target is `emulator-5592`, API 29, 412×915 mdpi.

### Executed

From `apps/android`, using `/opt/data/tools/android-env.sh`:

```sh
bash gradlew -PallowMissingNative=true \
 :service:testDebugUnitTest :service:lintDebug :service:assembleDebug :service:assembleDebugAndroidTest \
 :browser:testDebugUnitTest :browser:lintDebug :browser:assembleDebug :browser:assembleDebugAndroidTest \
 :denied:assembleDebug :denied:assembleDebugAndroidTest --console=plain --max-workers=2
```

Final actual output (`build-source-validation.log`, ignored local evidence):

```
BUILD SUCCESSFUL in 2s
261 actionable tasks: 20 executed, 241 up-to-date
```

- Service JVM JUnit XML: **2 tests, 0 failures, 0 errors, 0 skipped** (`CallerPolicyTest`, `RequestPolicyTest`, one behavior each).
- Both policy behaviors were written before implementation, executed against deliberately denying implementations, and observed assertion failures. `tdd-red.log`: CallerPolicy failed at line 9. `request-red.log`: RequestPolicy failed at line 6. Green reruns passed; final XML under `service/build/test-results/testDebugUnitTest/`.
- Instrumentation was written before its matching service/UI implementations, compiled and packaged, **not executed** because emulator ownership was reserved to the parent. Full native/UI RED→GREEN is therefore still an acceptance gate, not claimed here.
- Browser JVM task is `NO-SOURCE`; there are no Browser JVM tests to count as passing.
- Service lint: **0 errors, 4 warnings** (three pinned dependency-update suggestions; one legacy-backup declaration suggestion).
- Browser lint: **0 errors, 7 warnings** (six pinned dependency-update suggestions; one legacy-backup declaration suggestion).
- `bash -n validate-device.sh` passed.
- `javap` confirmed the required JNI declaration:

```
public static final native java.lang.String execute(java.lang.String, java.lang.String, java.lang.String);
```

- `apkanalyzer manifest application-id` returned the exact product IDs:
  - `com.everyonesoftware.storage.service`
  - `com.everyonesoftware.storage.browser`

### Built source-only APKs (not runnable until native rebuild)

Actual files/sizes at handoff:

| Artifact | Bytes |
| --- | ---: |
| `service/build/outputs/apk/debug/service-debug.apk` | 878141 |
| `browser/build/outputs/apk/debug/browser-debug.apk` | 17711971 |
| `service/build/outputs/apk/androidTest/debug/service-debug-androidTest.apk` | 922857 |
| `browser/build/outputs/apk/androidTest/debug/browser-debug-androidTest.apk` | 1079919 |
| `denied/build/outputs/apk/debug/denied-debug.apk` | 855684 |
| `denied/build/outputs/apk/androidTest/debug/denied-debug-androidTest.apk` | 891097 |

The native artifact directory did not exist when these checks ran. No native runtime success is implied by successful Kotlin/Java/APK tasks. The normal packaging guard was also exercised:

```sh
bash gradlew :service:verifyNativeLibs --console=plain --max-workers=2
```

Actual result: exit **1**, `Missing native core for x86_64; run repository scripts/build-native.sh first`. The explicit `allowMissingNative` property is a compile-only escape hatch; do not install/ship a missing-native Service APK.

### Ready for parent execution

1. Native worker finishes `artifacts/native/android/{x86_64,arm64-v8a}/libstorage_core.so`.
2. Rebuild **without** `-PallowMissingNative`; verify both native libraries in the Service APK and no `libstorage_core.so` in Browser.
3. On the task-owned emulator, run `bash apps/android/validate-device.sh emulator-5592` after sourcing the toolchain. The script operates only the explicit serial and validates exact JUnit summaries:
   - `StorageBinderTest`: **2 expected** — real generated descriptor import/list/exact read, new version and old-byte retention, rebind identity/bytes; native-command/path/actor rejection.
   - `DeniedCallerTest`: **1 expected** — real separate UID/package with same signer and signature permission but no package grant cannot query health.
   - `ColdRestartTest`: **1 expected seed + 1 expected verify**, with an external force-stop in between — stable IDs and exact retained bytes after process death.
   - `BrowserBinderTest`: **2 expected** — actual Browser UID/service/native fixture import → visible item → real attributes/history, plus truthful deferred Queries/update settings.
4. Script logs and real Browser grid/details screenshots land under `apps/android/build/device-validation/`. Inspect the images; merely writing a screenshot is not visual approval.
5. Exercise the native document import/export/open pickers, grid/list, collection flows, light/dark, error/reconnection states; document any runtime failures and corrections here.

No fabricated diagnostics, paired devices, seeded production records, release/update result, or networking state is used. No physical phone, replication, Windows runtime or full first-release acceptance is claimed.
