# Storage engineering instructions

Read `docs/architecture-proposal.md` and `contracts/first-slice.md` before changes. The user approved the proposed stack, initial Windows metadata coordinator limitation, and explicitly authorized offline pin reads with delayed remote revocation. Approval does not mean those features have been implemented.

## Boundaries

- `core/` is the shared Rust repository engine; only device services may call it.
- `apps/windows/` contains native WinUI Browser, logical client and separate Windows service.
- `apps/android/` contains separate native Browser/service APKs and the Binder logical client.
- `contracts/` owns logical semantics and cross-platform records. The service-private core ABI is not an application protocol.
- `scripts/` contains non-Python build and validation tooling. No Python project code, tests or scripts.
- Browser clients never read repositories, choose data stores, supply trusted identities, or implement replication.
- ItemVersions are immutable. AccessPolicies are independently revisioned and govern historical reads. Policies are desired state; Placement is verified observed state.
- Application capabilities and current Item policy are enforced by services/core, not merely UI.

## Workflow

Use this shared checkout. Preserve others' edits. Do not commit or push unless requested. Write failing behavior tests before implementation. Give parallel workers disjoint scopes and serialize builds sharing outputs. No emulator leases or host-wide serialization; use task-owned AVDs and explicit serials. Never stop another task's emulator or the shared adb server.

Source `/opt/data/tools/storage-env.sh` in this environment. Native compilation uses `bash scripts/build-native.sh`; component instructions live with each application. Run `cargo test --workspace --locked`, `cargo fmt --all -- --check`, and `cargo clippy --workspace --all-targets --locked -- -D warnings` once core exists. Runtime verification on each OS remains distinct from compilation.

Do not report native Windows runtime success from a Linux cross-build or Android hardware success from an emulator. Do not present test fixtures as production Items or claim pairing/replication before real two-device GUI verification. Preserve Windows native PRI generation and modern title-bar styling. Settings update checking must be honest until release artifacts/feed are implemented.

Release automation entrypoint: `.github/workflows/ci.yml` (validate, Android, Windows, serialized release). Compare each platform against its last verified publication; preserve unchanged outputs and source identities. See `contracts/release-automation.md` and `docs/RELEASING.md` for exact inventory, signing requirements, native acceptance and local tests. Creation of the local workflow is not authorization to commit/push, create signing keys, configure secrets or publish. After any authorized push monitor GitHub Actions for the exact SHA to completion.
