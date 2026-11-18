# Release-aware selection and publication

`contracts/release-automation.md` owns the release names, source identity and platform packaging requirements. `scripts/release.mjs` uses Node built-ins and `git`; GitHub operations use `gh` with argument arrays, no shell and no credentials in arguments. Music's actual `selective-builds.mjs`, `ci-selection.mjs` and `publish-windows-main-release.mjs` were inspected read-only as the reference pattern. No Music Python tooling was copied.

## Workflow CLI

```sh
node scripts/release.mjs version --github-output "$GITHUB_OUTPUT"
node scripts/release.mjs select --repository owner/repo --target auto --github-output "$GITHUB_OUTPUT"
node scripts/release.mjs manifest --platform android --directory dist/android
node scripts/release.mjs manifest --platform windows --directory dist/windows
node scripts/release.mjs publish --repository owner/repo --android-directory dist/android --windows-directory dist/windows
```

Each command prints JSON. `version` emits `count`, `version`, `tag`, `sha`; count is the full committed HEAD's reachable Git commit count, restricted to 1..65534. Shallow and unborn histories are errors, not development-version fallbacks. Fetch full history before invoking it. Source SHA is exactly 40 lowercase hexadecimal characters.

`select` emits Boolean `android`, `windows`, `android_changed`, `windows_changed`, plus `reasons`, a JSON object containing each platform's baseline, HEAD, affected paths and reason. GitHub output serializes those five fields as single-line values. `auto` builds affected platforms; `all`, `android`, and `windows` force exactly the requested platforms while retaining independently computed `_changed` fields. These modes do not turn malformed publication metadata into a successful selection.

Only pass a directory to `publish` when that platform's selected build, packaging, native verification and artifact download succeeded. Omit a failed/skipped platform entirely. With neither directory, publication returns `published: []` without GitHub calls or release creation. The final workflow must be serialized and have release-write permission; PR selection requires read permission only. A failed platform job must still leave the overall workflow failed even if the final publisher successfully attaches the other platform.

## Verified publication baselines

Selection reads stable `v0.1.N` releases in numeric descending order, separately for each platform. A release without that platform's `storage-<platform>-release.json` is not its baseline. A **present but malformed or incomplete manifest fails closed**; it never silently falls back to an older baseline.

A baseline requires the exact manifest schema, bounded expected file names, valid positive sizes and lowercase SHA-256 hashes; each declared asset must exist uniquely, be nonempty and uploaded, match the declared size and, when the API supplies a digest, match that digest. Manifest bytes themselves are downloaded and size/digest checked. The release's exact `target_commitish`, manifest source and dereferenced tag must agree. Annotated tag chains are bounded to 16, including cycle rejection. Release and asset inventory pages contain at most 100 records; 20 full pages are an error, not silently truncated success. API authentication, rate-limit and network errors are fatal. Only specifically optional release/tag lookups accept HTTP 404 as absence.

Large payloads are **not** downloaded during every selection: a complete verified publication marker plus asset metadata/digests is the baseline evidence. Publication performs actual remote payload downloads before writing the marker. Selection does not treat a CI run or preceding push as publication evidence, so failed builds remain eligible on later pushes.

Full Git ancestry is checked before diffing. A newer published source already containing the current source skips an older run; divergent or unavailable ancestry is an actionable error. Unchanged platforms retain their assets in their original release with their original version and source, never copied or relabeled into another release.

### Path classification

- `apps/android/` affects Android only; `apps/windows/` affects Windows only.
- Platform-named helpers/tests under `scripts/` or `tests/` affect that one platform when their path names exactly one of Android/Windows.
- `docs/`, `README*`, `AGENTS`, `VALIDATION`, and `RELEASING` documentation names are ignored.
- Shared core, bindings, contracts, Cargo/toolchain files, release tooling, unknown inputs and the **entire CI workflow** conservatively affect both platforms. No partial YAML job parser is used.
- Diffs use NUL-delimited paths and disable rename detection so both deletions and additions are considered.

## Manifest and checksum invariants

Each platform directory is flat and contains exactly its contract payloads plus `Storage-<platform>-SHA256SUMS` and `storage-<platform>-release.json`. Links (including hard links), directories, arbitrary extra files, unsafe paths, case collisions, duplicate names, empty files and oversized files are rejected. Individual payloads are bounded to 4 GiB; metadata downloads/reads are bounded to 64 KiB. Local hashes stream through a 1 MiB buffer and reject size/identity changes during hashing.

The sums file contains the primary payloads in contract order, with lowercase SHA-256, two spaces, filename and newline. It excludes itself and the JSON manifest. Windows includes the setup `.sha256` payload; that sidecar must itself name and hash the actual setup EXE correctly. The JSON `files` inventory includes the platform sums file, excludes the JSON manifest, and binds every record to the current immutable source/version. Android declares `signing: release-key`; Windows declares `signing: unsigned`.

Manifest generation refuses conflicting existing sums/manifests instead of overwriting them. Repeating it on unchanged valid bytes is idempotent. This helper validates inventory and integrity, **not** APK signing/package internals, Velopack archive structure, installer behavior or native GUI acceptance. Platform packagers must validate those before invoking `manifest`; a manifest label alone is not signing/runtime evidence.

## Publisher safety and repair

Publication requires a clean committed checkout (ignored build outputs are permitted), validates all supplied local directories, then copies them into task-owned temporary snapshots. It rechecks source cleanliness/identity before writes. Source-generated snapshots are removed on completion/error; original platform directories are not rewritten.

For each active platform it preflights all already-present assets by downloading and hashing them before any missing files are uploaded. Uploads use `gh release upload` without `--clobber`; there is no delete, replacement or overwrite path. Every upload is followed by fresh release/asset reads and downloaded byte verification. The manifest uploads **last**, only after all of that platform's payloads and checksum bytes were remotely verified. A second complete readback verifies final platform completion, rather than trusting a successful CLI exit. Partial uploads without the marker are repairable by an idempotent rerun. Conflicts require human investigation; the helper will not erase evidence to repair them.

Preexisting release metadata and every preexisting asset ID/metadata record are preserved. Only asset `download_count` is excluded from asset comparisons because verification and real users increment it. Release asset lists and automatic release timestamps are excluded from release comparisons; title/body, source, stability policy and other release fields are not. A wrong-source, draft or prerelease destination is rejected. Failures while uploading one platform do not prevent attempting the other; the command still exits unsuccessfully and reports which platforms completed verification.

A new destination is one normal stable `Storage 0.1.N` release with explicit unsigned-Windows and early-feature limitations. It is initially created with Latest disabled. Only a newly created, successfully populated numerically highest stable release may be promoted to Latest, with readback. Existing releases' title/body and Latest policy are never edited. An older late run cannot demote a numerically newer stable release. Unchanged-platform assets are neither copied nor invented; consumers should search each platform's last verified release rather than assuming Latest contains both.

GitHub offers no multi-asset transaction. Network failure after a verified-marker upload can leave a complete marker despite an unsuccessful final command; reruns verify and preserve it. No rollback deletes are attempted. The workflow's serialized release job is a required part of preventing concurrent publication/promotion races.

## Tests and exported interfaces

```sh
node --test tests/release.test.mjs
node --check scripts/release.mjs
```

Tests create real temporary Git repositories and commits outside the checkout. They require Node and Git only, not Android, .NET, `gh` authentication or live writes. GitHub behavior is a stateful command-boundary simulator with disposable fixture bytes: it is **not** evidence of actual publication, signing, native runtime or hosted CI success.

Exports for direct tests/integration are `deriveSource`, `createManifest`, `validateDirectory`, `validateManifest`, `githubClient`, `resolveTag`, `publishedBaseline`, `affectedPlatforms`, `decideFromBaseline`, `select`, `publish`, and `main`. Functions accepting `run` default to Node `spawnSync`; source-dependent helpers accept a fixture `root`. `main(argv, {root, run, output})` shares exactly the workflow CLI dispatch. No project dependencies or Python are required.
