# Storage Core

`storage-core` builds `storage_core` as an `rlib` and `cdylib`. The service-internal JSON envelope and operations follow `../contracts/first-slice.md`; all operations in that frozen local slice are implemented. The crate is not a public client SDK.

## Repository and transaction model

- `metadata.sqlite3`: SQLite schema version 1, foreign keys enabled, WAL, FULL synchronous mode, 10-second busy timeout. Initial schema and random stable device/store IDs are committed together. Newer unknown schema versions are rejected.
- `staging/<random UUID>`: ingestion streams the host-created source file through a 64 KiB buffer, hashes SHA-256 while writing, and syncs the completed stage.
- `blobs/<sha256>`: a completed stage is published atomically before metadata publication. Unix syncs the blob directory after rename; Windows uses `MoveFileExW(MOVEFILE_WRITE_THROUGH)` on the same-volume stage. An existing candidate blob is fully rehashed and length-checked before reuse; corrupt existing files are not overwritten or silently repaired.
- Each logical command holds a SQLite immediate transaction, including authorization and revision checks. Mutations and policy audit rows commit together. Errors close the connection and roll back outstanding metadata. This conservative first slice serializes reads/ingestion with writers; it is not a high-throughput concurrency design.
- A process exit before metadata commit can leave an unreferenced complete blob or private stage, never a visible partial Item. Startup does not delete unknown files. No automatic GC is implemented; crash-test hooks are absent from production builds.

Item mutations require their named capability and Item ownership. Readers get read access only through the current independent AccessPolicy, including historical versions. Policy edits require `manageAccess`, ownership, and the policy revision, not the Item revision. Policy edits do not change Item timestamps, Item revision, or version content. Policy changes are stored in the transactional `audit` table; this slice does not expose an audit-list operation.

Collections are owner-private references, not ownership containers. Membership reads independently filter each Item's current AccessPolicy. Removing/deleting a Collection does not delete Items or versions.

All four lists (`listItems`, `listCollectionItems`, `listVersions`, `listCollections`) return an explicit boolean `hasMore`, at most 500 authorized records, and a complete compact UTF-8 JSON success envelope of at most 400 KiB. The common limiter counts actual serialized record bytes (including Unicode, escaping, separators, and envelope overhead), stopping before the first record that would exceed the budget. Authorization is applied before count/byte limits; hidden records do not affect `hasMore`. SQL fetches at most 501 authorized IDs to detect count truncation; short records retain the 500-record behavior.

These are bounded prefixes, **not full pagination**: no continuation cursor or offset exists, and callers cannot retrieve the remainder through these list operations yet. `hasMore=true` means authorized records were omitted, whether for count or byte limits. The service-private core accepts strings larger than public hosts allow; if even the first record cannot fit, the list is empty with `hasMore=true` rather than skipping that record or overflowing transport. These bounds do not limit non-list responses or total repository work (for example SQL sorting/scanning).

## Trust boundary and limitations

The native service owns the root, source stages, identity authentication, app grants, filesystem permissions, and byte-stream IPC. Neither root nor `sourcePath` nor the trusted actor JSON may be client-controlled. Core does not authenticate OS callers or set platform ACLs. Import names and content types are stored as logical strings; generated hash/length attributes come only from actual ingested bytes. Content-type sniffing is not implemented.

`readVersion` verifies hash and length before returning a service-private blob path. Only the host may open/copy/stream it; never send that path to clients. The repository assumes trusted hosts do not mutate published blobs. Core cannot recall already returned bytes or independently reauthorize an in-progress platform-host stream after the command returns. `health` reports stable identities/counts and metadata availability, not an exhaustive blob scrub; verified reads detect corrupt/missing blobs.

This is local storage only: no pairing, replication, coordinator authority, cryptographic device identity, offline remote-revocation semantics, saved Queries, policy placement, transfer resumption, deletion API, encryption, repair, or garbage collection. Abrupt process-exit tests are not physical power-loss certification. Filesystem/hardware durability guarantees remain platform-dependent.

## Build and validate

```sh
source /opt/data/tools/storage-env.sh
export CARGO_TARGET_DIR=/opt/data/cache/scratch/storage-cargo-core
cargo fmt --all -- --check
cargo test --locked -p storage-core
cargo clippy --locked -p storage-core --all-targets -- -D warnings
cargo build --locked -p storage-core --release
```

The parent-owned `scripts/build-native.sh` assembles release libraries for platform hosts. See `VALIDATION.md` for actual execution evidence and `../bindings/storage_core.h` for C ownership rules. Android exports the exact static JNI symbol `Java_com_everyonesoftware_storage_service_NativeCore_execute`, with the same envelopes as the C ABI.
