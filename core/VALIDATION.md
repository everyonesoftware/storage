# Core verification evidence

Working checkout: `/opt/data/code/storage`. All host Cargo invocations used `CARGO_TARGET_DIR=/opt/data/cache/scratch/storage-cargo-core` and the installed Rust 1.100 toolchain. No Python project code or tooling was introduced.

## Test-first vertical slices

Each command below was actually executed. Initial failing slices returned exit code 101 and were followed by passing full-suite execution after implementation.

| Slice | RED command / observed failure | GREEN observation |
| --- | --- | --- |
| Stable store identity + capability | `cargo test -p storage-core --test repository identity_survives -- --nocapture`: 0 passed, 1 failed; UnsupportedOperation rather than successful health | Full suite: 1 passed |
| Import/list/read/private ownership | `cargo test -p storage-core --test repository import_reads`: 0 passed, 1 failed; UnsupportedOperation at createItem | Full suite: 2 passed |
| Immutable versions/revision mutations | `cargo test -p storage-core --test repository versions_are`: 0 passed, 1 failed; UnsupportedOperation at createVersion | Full suite: 3 passed |
| Independent policy and historical revocation | `cargo test -p storage-core --test repository policy_revisions`: 0 passed, 1 failed; UnsupportedOperation at setAccessPolicy | Full suite: 4 passed |
| Private reference Collections | `cargo test -p storage-core --test repository collections_are`: 0 passed, 1 failed; UnsupportedOperation at createCollection | Included in subsequent passing full suite |
| C ABI | `cargo test -p storage-core --test repository c_abi`: 0 passed, 1 failed; stub returned null | Full suite: 6 passed |
| Abrupt process interruption | `cargo test -p storage-core --lib recovery::process_exit -- --nocapture`: 0 passed, 1 failed; child exited 0 instead of fault status 86 | Child processes then exited 86 at staged/published/metadata checkpoints, with unchanged empty committed state after reopen |
| Last precommit boundary | Same command after adding the ready checkpoint expectation: 0 passed, 1 failed; child exited 0 instead of 86 at ready | Four process-exit boundaries now pass, including fully populated but uncommitted metadata |
| Forward schema protection | `cargo test -p storage-core --test repository newer_schema`: 0 passed, 1 failed; version-99 database incorrectly opened successfully | Unknown newer schema now rejected without rewriting its version |

Additional regression tests exercised already-implemented integrity checks, SQL-trigger-induced rollback, orphan retention/reuse, revision contention, all-operation capability denial, structured malformed input errors, and 501-item truncation.

## Final host gates

Executed after final production code changes:

```sh
source /opt/data/tools/storage-env.sh
export CARGO_TARGET_DIR=/opt/data/cache/scratch/storage-cargo-core
cargo fmt --all
cargo fmt --all -- --check
cargo test --locked -p storage-core
cargo clippy --locked -p storage-core --all-targets -- -D warnings
cargo build --locked -p storage-core --release
nm -D --defined-only /opt/data/cache/scratch/storage-cargo-core/release/libstorage_core.so
```

Actual results: formatting check exit 0; unit tests **2 passed, 0 failed** (one subprocess entry helper and one four-boundary crash test); integration tests **12 passed, 0 failed**; doctests 0; Clippy exit 0 with warnings denied; optimized release build exit 0. Exported symbols:

```text
0000000000084f40 T storage_execute
0000000000085460 T storage_free
```

The repository-required workspace equivalents were also executed afterward: `cargo test --workspace --locked`, `cargo fmt --all -- --check`, and `cargo clippy --workspace --all-targets --locked -- -D warnings`; all exited 0 with the same 2 unit/12 integration test results.

Host release artifact: `/opt/data/cache/scratch/storage-cargo-core/release/libstorage_core.so`. Debug and release rlibs are also produced in the isolated target directory.

The 12 integration tests cover:

1. C ABI real roundtrip, invalid UTF-8/null input, and free(NULL).
2. Stable UUID identities and capability-gated diagnostics on reopening.
3. All frozen operations deny missing capabilities; malformed input and unsupported operations are structured errors.
4. Import/list/get/read actual disk SQLite and blob content; owner-private filtering.
5. Corrupt/missing blob reads and corrupt-blob dedup reuse fail closed, with no new metadata.
6. Metadata failure after blob publication rolls back Item, version and policy; orphan retained and later verified/reused.
7. Two simultaneous revision mutations produce exactly one winner and one RevisionConflict.
8. Independent policy revision/audit and revocation across current and historical versions; reader cannot take ownership or mutate policy.
9. Owner-private Collection references, independent Item access revocation, remove/delete without Item deletion.
10. Unique immutable versions including identical bytes; historical content retained; stale version/rename rejection.
11. 501 real Items produce a bounded 500-entry response with hasMore=true and one shared verified blob.
12. Newer schema version is rejected without modification.

## Separately linked C ABI conformance

Compiled the parent-owned `tests/conformance/abi-driver.c` using `cc -Wall -Wextra -Werror`, linked dynamically against the above optimized shared object, and ran the parent-owned `tests/conformance/native-abi.test.mjs` unchanged except for an in-memory substitution of the driver's path. No parent artifacts or test source files were modified.

Driver artifact: `/opt/data/cache/scratch/storage-core-abi-driver`.

Actual result: **5 tests, 5 pass, 0 fail, 0 skipped**, exit 0. Covered arbitrary binary content larger than 1 MiB with SHA-256 verification, persistent IDs across separate C processes, immutable historical versions/stale revision, capability and current policy revocation, Collection deletion preserving Items, missing source, and tampered published bytes.

## Android compilation check

`cargo check --locked -p storage-core --target aarch64-linux-android` passed after configuring `CC_aarch64_linux_android` to the NDK API-26 clang and `AR_aarch64_linux_android` to NDK llvm-ar. This compiles the conditional JNI implementation. The first attempt without these target variables failed in bundled SQLite looking for `aarch64-linux-android-ar`; this was an invocation configuration issue, not a fabricated success.

Full Android/Windows cross-build outputs and native host runtime verification belong to the parent/platform workers. This record does not claim JNI execution on a device, Windows DLL execution, physical power-loss testing, replication, pairing, or full first-release acceptance.

## Independent-review fix: bounded listing response bytes

All commands for this follow-up used `source /opt/data/tools/storage-env.sh` and `CARGO_TARGET_DIR=/opt/data/cache/scratch/storage-cargo-review-fix`. Changes were confined to `core/`; no manifests, platform builds, or external conformance files were changed.

Strict test-first tracer bullets, each observed RED before its production change and then GREEN:

| Test filter (`cargo test --locked -p storage-core --test repository <filter> -- --nocapture`) | RED (exit 101, 0 passed / 1 failed) | GREEN (exit 0) |
| --- | --- | --- |
| `item_listing_bounds_serialized_bytes` | Complete `listItems` success envelope was **2,005,548 bytes**, above the asserted 400 KiB limit | Common byte-aware limiter introduced; both `item_listing` tests passed, including the unchanged 500-entry expectation |
| `collection_item_listing_bounds` | Complete `listCollectionItems` success envelope was **1,877,548 bytes** | Collection membership listing uses the same limiter; 1 passed |
| `version_listing_preserves` | Returned **501** versions instead of the expected **500** | SQL 501-ID lookahead and common limiter; 1 passed, including exact-500 `hasMore=false` |
| `collection_listing_bounds` | Returned **501** Collections instead of the expected **500** | SQL 501-ID lookahead and common limiter; 1 passed for short and maximum-length escaped names |

Fixtures create real Items/versions/Collections through `execute`, with disk SQLite and blob ingestion. Large Item tests use the exact public Windows 1024-UTF-16-unit name/contentType lengths and Android 512/256 lengths. Unicode and JSON-escaped control characters exercise actual encoded byte lengths rather than character counts. Every large-list regression measures the complete compact JSON success envelope against 400 KiB, safely below the Android 512 KiB response bound. All four list results now always include boolean `hasMore`.

Further regression coverage strengthens the original 501-Item test (without lowering its 500 expectation) with 500/501 membership count boundaries and exact-full `hasMore=false` for both Item lists. Existing Collection revocation now asserts the entire empty result has `hasMore=false`; private large Item/Collection results do not leak truncation to other principals, and private version/membership access is denied. A separate oversized-single-record regression proves private-core strings beyond public host limits are omitted with `hasMore=true`, not emitted above budget.

Final executed gates after all code/test changes:

```sh
source /opt/data/tools/storage-env.sh
export CARGO_TARGET_DIR=/opt/data/cache/scratch/storage-cargo-review-fix
cargo fmt --all
cargo fmt --all -- --check
cargo test --workspace --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
```

All exited **0**: **2 unit tests + 17 integration tests passed**, 0 failed; 0 doctests; formatting clean; Clippy clean with warnings denied. Native target rebuilds and platform runtime rechecks are deferred to the parent, not claimed by these host gates.

Limits remain explicit: this is bounded-prefix truncation, **not cursor/offset pagination**. Records beyond the prefix cannot yet be fetched through list continuation. A first record larger than the entire budget yields an empty list with `hasMore=true`. Only list response bytes/counts are bounded here; non-list responses and SQL scan/sort costs are not globally bounded. Repeatable validation workflow: reproduce using public-host-maximum metadata, serialize the whole success envelope, check both byte and count boundaries, and verify authorization filtering before limits—not merely a row count cap.
