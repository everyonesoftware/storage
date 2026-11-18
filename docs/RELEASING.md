# Building and publishing Storage

## Workflow shape

`.github/workflows/ci.yml` follows the current Music release pattern:

1. **validate** tests orchestration and the real shared engine, then selects platform builds against each platform's last verified publication.
2. **android** builds the native engines, runs unit/lint and real Binder/Browser emulator checks, and produces two release-key-signed APKs on main.
3. **windows** builds the Rust DLL natively with MSVC, tests the native client/service, publishes the complete WinUI/service payload, builds Velopack output and gates it on installed-app lifecycle verification.
4. **release** is the only job with write permission. It serializes publication, downloads only successful platform outputs, verifies their source/bytes, and attaches them to one normal `v0.1.N` Storage release. No separate Windows preview release is created.

Pushes to main run automatically. Pull requests validate/build but never publish or receive release-signing secrets. Manual dispatch offers `auto`, `all`, `android`, or `windows`; the named platform/all choices force those builds.

A failed platform never advances its publication baseline. Another successful platform may still publish; the overall workflow remains failed because of the failed build. Unchanged platforms keep their original release assets, source SHA and version. They are not copied or relabeled into a newer release. A docs-only/no-op push creates no empty release.

## Signing configuration and remaining activation

The repository was empty and had no signing secrets when this workflow was prepared. Dan separately authorized creation/configuration on November 18, 2026. A dedicated Storage RSA-4096 signing identity was generated and all four repository Actions secrets below were uploaded and verified through their exact GitHub metadata endpoints. A temporary APK was actually signed and its signer verified locally; the temporary APK was removed. Signing setup did not itself commit, push or publish a release. Activation requires the committed workflow to be pushed and its exact-source hosted run monitored and verified.

The configured repository secrets are:

| Secret | Meaning |
|---|---|
| `STORAGE_KEYSTORE_BASE64` | Base64 bytes of the release keystore |
| `STORAGE_KEYSTORE_PASSWORD` | Keystore password |
| `STORAGE_KEY_ALIAS` | Key alias |
| `STORAGE_KEY_PASSWORD` | Private-key password |

Both product APKs must share the same key because signature permission and certificate checks secure service IPC. Back up the private key securely and retain it for future updates. Do not reuse Music's key by assumption, send key material/passwords in chat, commit a keystore, or substitute debug signing if a secret is absent. The build fails with a specific missing-secret error instead.

Local recovery files are outside the checkout at `/opt/data/.local/share/storage/signing/`: the password-protected `storage-release.jks` and private `signing-credentials.json`, with directory mode 0700 and file modes 0600. The credential JSON is not encrypted; protect the host account and make an independent secure backup of both files. Never attach these files to a release. Do not regenerate or rotate the signing identity merely to rerun setup. Public certificate SHA-256: `5A:45:B1:45:28:9A:2D:51:7C:94:D7:CC:C3:6B:DD:31:3D:6C:93:F0:32:9A:3C:F4:F1:C0:4F:0D:31:7A:98:59`.

For authorized recovery/reconfiguration using the **same** prepared keystore, the authenticated GitHub CLI can upload it without printing its encoded contents:

```bash
gh secret set STORAGE_KEYSTORE_BASE64 --repo everyonesoftware/storage < <(base64 -w 0 /secure/path/storage-release.jks)
gh secret set STORAGE_KEYSTORE_PASSWORD --repo everyonesoftware/storage
gh secret set STORAGE_KEY_ALIAS --repo everyonesoftware/storage
gh secret set STORAGE_KEY_PASSWORD --repo everyonesoftware/storage
```

The last three commands prompt locally. Do not paste values into a transcript. Secret setup is a separate explicitly authorized action. The workflow decodes the keystore into the runner's private temporary directory and removes it with an EXIT trap; key material is never part of an uploaded artifact.

No paid Windows signing certificate is required. The Windows installer is explicitly unsigned; SmartScreen/unknown-publisher warnings are expected, and unsigned does not mean a separate GitHub prerelease. The native Windows smoke gate must pass before its inventory is eligible for publication.

## Version and assets

Versioning derives from complete committed history: `0.1.<git rev-list --count HEAD>`, tag `v0.1.<count>`. Both Android APKs use that count as versionCode. Native application version components restrict the common count to 1..65534. Shallow or unborn history is rejected for releases; no timestamp/run number silently replaces source identity.

Expected Android user downloads:

- `Storage-Service-0.1.N.apk` — install first.
- `Storage-Browser-0.1.N.apk`.

Expected Windows/update inventory:

- `StorageWindows-win-x64-Setup.exe` and its `.sha256` sidecar.
- `StorageWindows-0.1.N-win-x64-full.nupkg`.
- `releases.win-x64.json`, `assets.win-x64.json`, `RELEASES-win-x64`.

Each successful platform also publishes its own `Storage-<platform>-SHA256SUMS` and `storage-<platform>-release.json`. Distinct names prevent Android and Windows checksum/manifest collisions in the shared release. The manifest records exact source commit, version, sizes and hashes. It is uploaded only after payload verification, so an incomplete platform upload is not a successful baseline.

Published assets are immutable. Reruns may repair missing assets using identical verified bytes; they must not overwrite conflicting existing bytes. Do not force a rebuild at the same version to replace a published binary; commit the changed build inputs to obtain a new version. Release-only reruns can reuse the original run's platform artifacts.

## Installer and authorization boundaries

A Velopack package is not permission to bypass Storage's per-application authorization. Windows first-party authorization must remain service-owned and explicitly consented, with connection-derived identity checks and a narrowly bound grant. The installer lifecycle gate verifies the real installed Browser and independent service rather than approving a process-only launch or granting every same-user caller.

The initial Android debug APKs already delivered are signed with a development certificate. A production-key-signed build cannot silently update those installations. Export any needed content before manually transitioning; uninstalling/clearing Storage Service deletes its local repository. There is no automatic debug-to-release repository migration.

A published update feed is not proof of implemented in-app updating. Settings behavior and any remaining update-flow limits are documented in each platform README; do not infer check/download/restart acceptance from packaging alone.

## Local checks and hosted acceptance

```bash
node --test tests/ci-workflow.test.mjs tests/release.test.mjs tests/android-release.test.mjs tests/windows-release.test.mjs
node scripts/lint-workflow.mjs --actionlint /path/to/pinned/actionlint
```

Pinned actionlint 1.7.12 does not yet recognize GitHub's documented `concurrency.queue: max`. The lint wrapper checks the exact supported serialized-release block itself and suppresses only that unknown-key diagnostic; all remaining workflow errors still fail. Queueing up to the documented limit prevents the default one-pending-run replacement from silently discarding a pending release job. Reference: `https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax`.

The platform build, package-validation and Windows lifecycle scripts are executable gates, not claims that hosted CI has run. A workflow cannot run in an empty remote repository. After an authorized commit/push, monitor the exact pushed SHA to completion and verify the resulting release version, assets and downloaded bytes before calling publication successful.

Local verification on November 18, 2026: all 39 release/workflow tests passed with `STORAGE_ANDROID_GRADLE_TESTS=1` (no skips), including real Gradle configuration refusals, PowerShell parsing and recursive archive corruption checks. The parent also exercised the real Android SDK packager against disposable-test-key-signed APKs and verified actual debug-signer/wrong-version refusals. Windows client/service integration passed 43 assertions against the real Linux core; WinUI/service cross-builds had zero warnings/errors. Rust's 19 test entries, formatting, warnings-denied Clippy and 5 independent C ABI tests passed. None of these results is native Windows installer execution, physical-phone acceptance or GitHub publication. The Windows smoke gate now deliberately begins uninstall with the independently running, healthy service still active, so the actual uninstall hook must stop it on the hosted Windows runner.

GitHub's normal Actions token is used; only the final release job requests `contents: write`. Organization/repository policy must permit that requested permission. There is no PAT, cloud storage service or external publishing service requirement.
