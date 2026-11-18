# Current platform downloads

The root README and newest stable numeric Storage release share a generated **Current downloads** table. Windows and Android are resolved independently from verified published platform inventories, not from whichever assets happen to be attached to GitHub Latest. Unchanged platforms retain their original release URLs, versions and source identities. Discovery never rebuilds, copies or relabels binaries.

## Source of truth

`scripts/current-downloads.mjs` uses the existing Storage release contract and paginated GitHub release/asset discovery. Only stable, non-draft `v0.1.N` releases are eligible. A platform requires its complete manifest-backed inventory, valid source/tag identity and matching published size/digest metadata. Malformed published manifests fail closed rather than silently substituting an older release. Publishers remain responsible for actual payload-byte verification; a download table is not additional native runtime acceptance or an in-app update feed.

- **Windows x64:** the original installer and its SHA-256 sidecar. Windows installers are unsigned; unknown-publisher/SmartScreen warnings are expected.
- **Android:** both release-key-signed product APKs and the platform SHA-256 inventory. Install **Storage Service before Storage Browser**. The two APKs must share the Storage release signing identity.
- An unpublished platform is explicitly marked unavailable. Storage's approved initial scope has no web client; there is no invented web download.

These are early local-storage releases. Pairing, replication, distributed acceptance and in-app automatic updating are not implied by a published installer. Existing development-signed Android installations cannot silently update to the release key; see [release instructions](RELEASING.md#installer-and-authorization-boundaries) before transitioning or removing a service with local data.

## Read-only preview

From the checkout, with authenticated `gh` available:

```sh
node scripts/current-downloads.mjs --repository everyonesoftware/storage --output-directory "$TMPDIR/storage-download-preview"
```

Preview writes local review artifacts only; it does not change GitHub, commit a README or publish a release. The generated README preserves the local document outside `<!-- current-downloads:start -->` / `<!-- current-downloads:end -->`. Inspect the generated inventory and release notes alongside the README before authorizing publication.

## Automated refresh

The existing `.github/workflows/ci.yml` refreshes tables in its serialized final `release` job after platform publication. Main-branch no-op/docs-only runs can repair stale tables without creating a new binary release. There is no extra push-triggered workflow or platform rebuild solely to update a table. The same release-publication lock covers publisher and refresh writes. Other visible active main CI runs defer refresh; the owning run is excluded only after its platform dependencies have finished. A later main run retries deferred work.

The workflow uses the repository `GITHUB_TOKEN` for README updates, so its generated README-only commit does not recursively trigger another push build. Branch protection must permit this update; a rejected Contents API write is a visible failure, not a successful refresh. The current default-branch README is read remotely before publication rather than overwriting it with a stale checkout.

A deliberate live refresh requires separate authorization because it edits a release description and creates a default-branch README commit:

```sh
node scripts/current-downloads.mjs --repository everyonesoftware/storage --publish
```

Do not run `--publish` merely to preview the table. A personal token's README commit can trigger normal push workflows, unlike a repository Actions token. Local changes are not activated until an authorized commit/push and verified exact-SHA workflow run.

## Preservation and concurrency limits

The updater replaces only the managed block and preserves surrounding README/release prose. Missing, duplicate or broken managed markers are handled conservatively. README updates use the Contents API's existing blob SHA as an optimistic precondition. External writes are read back before success is reported; idempotent reruns avoid unnecessary changes. Asset contents, source identities, release titles, draft/prerelease flags and Latest policy are not edited by table refresh.

All automated writers must share `storage-release-publication`; do not overlap manual CLI refreshes with CI. **Do not edit release descriptions manually while a refresh is running.** A fresh pre-PATCH read rejects observable changes and post-PATCH readback verifies the result, but release-body GET/PATCH is not atomic against a human edit between requests. Do not claim a supported release-body compare-and-swap or invent `If-Match` guarantees. A later run can repair partial completion, but cannot recover an undetected overlapping manual edit.

## Validation

```sh
node --test tests/current-downloads.test.mjs tests/release.test.mjs tests/ci-workflow.test.mjs
```

Tests use disposable fixtures for write paths. Live previews are read-only. No native rebuild, emulator or Windows runtime claim is needed for a documentation-table change; existing platform publication gates remain separate.
