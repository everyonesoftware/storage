# Release automation integration contract

This task adds one Music-pattern CI workflow without committing/pushing the currently uncommitted application. Music is read-only reference material. New tooling uses Node, PowerShell and existing platform build tools, not Python.

## Pipeline

Single `.github/workflows/ci.yml`: `validate`, `android`, `windows`, `release`. One build job per platform; platform builds run in parallel after shared validation. PRs validate/build but never publish or read release signing secrets. Main pushes and manual main dispatch publish successful selected platform outputs via one serialized final job. Selection compares source inputs against each platform's last verified publication, not the preceding push. Unchanged outputs remain in their original release with original version/source identity; never copy/relabel/rebuild just to fill a new release. Manual component choices: auto/all/android/windows; all/android/windows force chosen platform(s). Docs-only no-op pushes produce no release.

## Version and manifest API owned by parent

`node scripts/release.mjs version [--github-output PATH]` derives `count`, `version=0.1.<count>`, `tag=v0.1.<count>`, and `sha` from a full, committed Git history. Refuse unborn/shallow history and count outside 1..65534. Native engines can retain Cargo crate version; application binaries and manifests use the derived release version.

`node scripts/release.mjs manifest --platform android|windows --directory PATH` validates actual output names, derives current source identity, writes platform SHA256SUMS and `storage-<platform>-release.json`. Publication revalidates local bytes, source/tag, exact platform inventory, and verifies downloaded remote bytes after writes.

Manifest schema: `{schemaVersion:1,platform:"android"|"windows",version:"0.1.N",count:N,tag:"v0.1.N",sourceCommit:"<40 lowercase hex>",signing:"release-key"|"unsigned",files:[{name,bytes,sha256}]}`. Files lists every platform payload plus its platform checksum file, excluding the manifest itself. Flat names only; no paths, links, duplicates or arbitrary files. Sidecar and sum files use lowercase SHA256 and two spaces before filename.

## Android owned outputs

`dist/android/Storage-Service-0.1.N.apk`
`dist/android/Storage-Browser-0.1.N.apk`
Parent generates `Storage-android-SHA256SUMS` and `storage-android-release.json`.

Both product APKs must share the actual release signer. Use four secret names: STORAGE_KEYSTORE_BASE64, STORAGE_KEYSTORE_PASSWORD, STORAGE_KEY_ALIAS, STORAGE_KEY_PASSWORD; never reuse Music's identity or substitute debug signing. No repository secrets currently exist. Missing secrets on a release-producing job must give a clear error and fail closed. Do not generate/publish private signing keys unless separately authorized. Gradle property `storageCommitCount` sets both product versionName/versionCode. First local developer build without history may retain 0.1.0/code1, but release builds require a positive explicit/derived committed count and complete signing configuration.

New portable helper `bash scripts/build-android-native.sh` builds only required Android engines, independent of Windows cross tools. Windows host builds its own native Rust DLL using MSVC. Keep existing local all-target script working. Android job owns unit/lint/native-containing builds and, if configured, instrumented checks inside the same job.

## Windows owned outputs

Under `dist/windows/`:

- `StorageWindows-win-x64-Setup.exe`
- `StorageWindows-win-x64-Setup.exe.sha256`
- `StorageWindows-0.1.N-win-x64-full.nupkg`
- `releases.win-x64.json`
- `assets.win-x64.json`
- `RELEASES-win-x64`

Parent generates `Storage-windows-SHA256SUMS` and `storage-windows-release.json`.

Use Music's actual Velopack 1.2.161 packaging pattern and normal stable release; unsigned Windows installer is allowed with explicit disclosure, not a separate Windows prerelease. Add actual startup hook with auto-apply disabled. Full package includes native WinUI/.NET payload, resources.pri, separate Service and its native DLL. Validate recursive payload inventory rather than merely feed hashes. Native hosted Windows job must build, test, package, install, launch a real rendered Browser and validate independent service IPC, then clean up; refuse publication on smoke failure and retain evidence with hard timeout.

Do not silently weaken existing application authorization to make installer run. Provide a legitimate service-owned first-party authorization consent path if unpackaged Velopack execution needs it; consent must default deny, persist a narrowly bound bundled-Browser grant, and OS-derived caller checks remain mandatory. No generic same-user allowlist, client-self-enrollment, raw internal command tunneling or fake smoke success. Installer lifecycle tests may approve only the task-installed service's own consent dialog on an isolated runner. Existing dev enrollment remains explicit and documented.

Settings auto-update flow is not implied by packaging a feed. Do not claim in-app check/download/restart until actually wired and tested. Preserve service/repository state across installer uninstall and lifecycle cleanup; test-owned temporary data only. No legacy Inno/MSIX migration is necessary because Storage has never published an installer.
