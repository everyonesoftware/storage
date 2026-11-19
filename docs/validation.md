# Integrated local-slice validation

## Compact native Settings redesign — November 19, 2026

The approved Windows **Storage, refined** and simplified Android Settings designs
are implemented in native WinUI/Compose, with on-demand, exact-source changelogs.
The Android app omits the retired header/refresh action and Updates, package
versions, About and Appearance sections. Both platforms retain real updater
verification and explicit installation actions; mockup controls/data are not shipped.

Final parent-run gates after the reviewed fixes:

- Windows portable/native-core integration: **97 assertions**, no failures/skips;
  Service and real-reference WinUI Linux cross-builds: **0 warnings/errors**.
- Android final native-containing products/test APKs, unit tasks and both lints:
  passed. Parsed final JVM reports: **46 Browser + 5 Service tests**, no
  failures/errors/skips. The APK pair verifies with the same existing debug signer;
  Service alone contains the actual core and descriptor shim for both ABIs.
- Task-owned API-29 `storage-settings-native29`, `emulator-5590`, 412×915 at density
  160: the complete final `validate-device.sh` passed **39 tests in 13 groups**.
  This includes eight native update-panel cases, real Settings navigation/removals,
  permission refusal, Service-first real OS session/consent, cleanup, denied callers,
  Binder content integrity and cold persistence. No completed package upgrade is
  claimed in this task. Fixture-state card screenshots are explicitly test evidence.
- Full Node suite including actual Gradle release-configuration refusal checks:
  **97 passed, no skips**. Rust workspace **19 tests**, formatting,
  warnings-denied Clippy, and **5 C ABI checks** passed.

Independent review found an Android subject-only filtering defect. The correction
uses complete, bounded per-commit changed-file metadata (including rename sources),
retains shared changes regardless of subject scope, and rejects incomplete history.
The final 301-commit test exercises all 305 credential-free compare/file requests.
The final independent read-only re-review passed with no remaining reported security
or logic concerns in the corrected changelog backends.

Pre-push inspection also found the preceding hosted Android run had exhausted its
16 MiB heap while hashing an entire APK fixture. Instrumentation fixtures now stream
hashes and swapped-package copies without changing updater verification assertions.
The actual compiled Kotlin helper verified an 18,506,540-byte APK under `-Xmx16m`;
the whole-file control failed with `OutOfMemoryError`. The complete 39-test native
suite and 97-test Node suite passed again after this test-only correction. Logs:
`/opt/data/cache/scratch/storage-settings-{heap-red,prepush-device,prepush-node}.log`.

An initial API-35 run passed the earlier panel/settings/installer groups but was
interrupted when revoking an install-source app-op killed the instrumented UID.
Grant/restoration now runs outside that instrumentation, with tested failure cleanup.
The unaccelerated API-35 image subsequently showed a System UI ANR under host
pressure; it was stopped without touching another task's emulator. Final complete
runtime evidence above is API 29 and light theme only, not final API-35/dark acceptance.

Development APKs, exact hashes and a machine-readable evidence summary are under
`artifacts/settings-redesign/android/`. They remain **0.1.0 / code 1**, development
debug-signed, and are not an in-place update for production-signed installations.
Do not uninstall or clear an existing Service to bypass a signature mismatch.

Windows installer packaging and native rendering/upgrade acceptance remain blocked
by the lack of an accessible Windows build host. No native Windows installer,
physical-phone success, production-signer upgrade, commit, push or publication is
claimed. Root download tables and published assets were not changed.

Parent logs are `/opt/data/cache/scratch/storage-settings-{device-final,windows-final,
node-complete,final-android}.log`; final per-group device logs are under
`apps/android/build/device-validation/`. Historical repeat logs in that directory
are not included in the final 13-group count.

## In-app updater integration — November 18, 2026

The working tree implements user-initiated Windows and Android Settings updates;
see [the update contract](../contracts/application-updates.md). Windows updater
and existing transport/core harness passed **56 assertions**, and Service/WinUI
cross-builds passed with zero warnings/errors. Cached package size/SHA-256 is
checked after download and before restart; corrupt cache rejection and fresh
retry were executed. This is not native Windows installed-upgrade acceptance.

Android's final native-containing build/unit/lint gate passed; parent parsed
**23 Browser + 5 Service JVM tests**, with no failures/errors/skips. The owned
API-29 device gate passed **31 executions**. Actual Settings and OS confirmations
upgraded debug Service first and Browser second from code 1 to code 2. Decline/retry,
pending-consent process death, cold repository identity/byte continuity and
Browser-grant continuity were exercised. Parent verified installed APK hashes
against final fixtures and byte-identical identity snapshots. Full disclosures
and retained failed atomic-path evidence are in
[Android validation](../apps/android/VALIDATION.md).

The final parent-run Node release/workflow/updater suite, including real Gradle
release-refusal tests, passed with no skips. Pinned workflow lint and diff checks
passed. Rust workspace tests, formatting, warnings-denied Clippy and five C ABI
tests passed during this task. Later Windows tray/activation changes appeared
in the shared checkout and were preserved; the full Node suite includes their
guards, but this updater task does not claim their native acceptance.

Remaining updater acceptance: real installed Windows upgrade/restart, Android
API 26/API 31+ and physical hardware. No updater-enabled release was published;
existing placeholder versions need one manual in-place upgrade after publication.

## Earlier local-slice evidence

Verified on November 17, 2026 in `/opt/data/code/storage`. These are actual parent-run integration gates after the independent-review fixes, not the earlier worker source-only builds. This is not full first-release acceptance.

## Results

| Gate | Actual result |
|---|---|
| Rust repository | 17 integration tests + 2 unit-test entries passed; the unit entries include the subprocess helper and four-boundary abrupt-exit test |
| Rust format / Clippy | Passed; Clippy warnings denied |
| Independent C driver → release core | 5 conformance tests passed, no skips |
| C# client → real pipe dispatcher → release core | 31 assertions passed, no skips |
| Windows service x64 compilation | Passed, 0 warnings/errors |
| WinUI actual-project Linux compile gate | Passed, 0 warnings/errors; not native launch |
| Native engine outputs | Linux x64, Windows x64 DLL, Android arm64 and Android x86_64 built |
| Android native-containing build | Passed without missing-native escape hatch; both lint tasks passed |
| Android JVM | 5 tests, no failures/errors/skips; Browser JVM has no tests |
| Android instrumentation | 16 executions passed across seven selected test invocations; includes both phases of cold persistence verification |
| Android signature and packaging | Both product APKs verify with the same development certificate; engine/shim present only in Service; 16 KiB service APK alignment check passed |

The Android executions comprise service Binder 8, denied caller 1, persistence seed 1, persistence verify 1, real process-death same-client reconnect 1, partial-list notice UI 2, Browser/service GUI 2. The script requires the exact expected JUnit totals and rejects negative test statuses, skips and crashes.

Target: task-owned `storage-initial-api29`, serial `emulator-5592`, x86_64 API 29, 412×915 at density 160, software emulation. No physical phone is claimed. No other task's emulator or shared adb server was stopped.

## Direct graphical file-picker check

In addition to instrumentation, the parent operated the actual Android Browser and native DocumentsUI:

1. Placed an explicit development document in the emulator's Downloads folder.
2. Chose **Import file → Downloads → Storage local check.txt** in the graphical UI.
3. Observed the resulting Item, generated MIME type, size, SHA-256, stable IDs and revision in Browser.
4. Chose **Export current version**, used the graphical destination picker, and named a new Downloads destination `Storage exported check.txt`.
5. Read back the visible **Export complete** notice, pulled that exact exported destination and compared it with the original using `cmp` and SHA-256.

Actual comparison: **226 bytes, identical**. Both SHA-256 values were `529aa7ced5dd0169669fda5ec218404c1ad60b6407e6a46229f0ae6c46dc369a`.

Screenshots under `artifacts/deliverables/` show the actual imported Item and inspector/export result. They contain an explicitly created development test document, not fabricated production data or a design mockup. Dark-theme runtime verification is not claimed: this API-29 image locks system night mode off. Historical version bytes were independently verified through core, C ABI, C# service and actual Android Binder tests; historical export through the Android graphical picker has not been separately exercised.

## Review corrections

Final narrow independent review passed with no remaining reported security or logic concerns in the corrected ownership helper and callsite. This supplements, rather than replaces, the executed tests and the documented platform limitations.

The initial independent review identified oversized list responses, starvation from idle import descriptors, and stale established bindings after death. Fixes add a 400 KiB complete-envelope budget/500-record cap with explicit truncation, bounded nonblocking descriptor staging with deadlines and cleanup, and generation-aware binding reset/reconnect. C# page APIs preserve truncation; array conveniences refuse to silently return incomplete histories/collections. Both Browsers display partial-list states; continuation pagination is still absent.

The second review identified a resource-ownership race in timed descriptor reads: cancellation of a Future could discard a descriptor produced later by an uninterruptible JNI call. The new `awaitOwnedOperation` keeps the resource in a synchronized ownership slot rather than inside a cancelled Future's result. Two regression tests failed against the extracted old behavior, then passed with deterministic late-result and completion/timeout-race cleanup; a third verifies successful caller ownership. The full JVM gate now contains 5 passing tests, and all 16 device executions passed again with the rebuilt Service APK. A software-emulator QEMU watchdog crash interrupted the first reinstall attempt; only the task-owned emulator was restarted, then the exact runtime gate was rerun successfully.

Parent runtime testing also caught Kotlin `run` scope-function shadowing of a private helper. Renaming it to `runStorageOperation` restored actual IO dispatch and structured error conversion. An ambiguous GUI test selector was scoped to the clickable noneditable Item card. The initially pinned Compose runtime produced an intermittent teardown SlotWriter crash; after pinning BOM `2026.03.01` (runtime 1.10.6), three repeated Browser class runs and the final full runtime pass succeeded. This is observed validation, not proof that all lifecycle bugs are impossible.

## Deliverables and reproducibility

Product files:

- `artifacts/deliverables/Storage-Service-0.1.0-dev.apk`
- `artifacts/deliverables/Storage-Browser-0.1.0-dev.apk`
- `artifacts/deliverables/validation-manifest.json` — parsed test totals, exact artifact sizes and hashes, and explicit runtime boundaries.

Install Service first, then Browser. These are development debug-signed APKs, not production-signed releases. Use disposable test files; this early build has no migration/update/release guarantee. Uninstalling/clearing the Service erases its local repository. No test or denied-caller APK is part of the deliverable.

Root build/test instructions are in `README.md`. Raw parent logs are in `artifacts/validation/`; component logs are in `apps/windows/validation/` and `apps/android/build/device-validation/`. The manifest collector rechecks actual logs and byte-identical delivered/tested APK hashes:

```sh
node scripts/collect-validation.mjs <GUI-imported-source-file> <GUI-exported-file>
```

All work remains uncommitted and unpushed. No GitHub Actions/release success is implied.

## Remaining gates

- Windows native DLL execution, service authorization APIs, WinUI launch, pickers/drop, resources.pri publishing, installation and visual acceptance require a Windows target.
- Windows currently has explicit development enrollment only for unpackaged execution; no signed production package/installer is supplied. Its initial UI is dark-styled; Windows light-theme acceptance remains open.
- Android physical arm64 phone, dark-theme appearance, long real-world provider transfers, graphical historical-version export and collection management need further manual coverage.
- Pairing, network metadata authority, secure peer transfer, replication, actual Placement, Policies/pinning, saved Queries, user-configurable third-party app grants, event subscriptions, content-type sniffing and production update feeds remain unimplemented.

The repository engine and native local applications are foundations for those slices, not substitutes for the requested two-device demonstration.
