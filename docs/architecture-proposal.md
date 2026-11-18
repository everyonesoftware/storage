# Storage: proposed first-release architecture

Status: architecture approved by Dan on November 17, 2026. Approval includes the recommended stack, initial Windows metadata coordinator limitation, and explicitly authorized offline pin reads with delayed remote revocation. Implementation is in progress; approval is not evidence of completed features. Sections retaining proposal wording below describe the original decision rationale.

## Scope and environment

The supplied engineering brief is the product specification. The initial product has native Windows and Android Storage Browser clients, independent device services, and no required cloud service. A web client is not part of the requested initial demonstration.

The checkout at `/opt/data/code/storage` has an unborn `main` branch, no application files, and origin `https://github.com/everyonesoftware/storage`. Inspection found Go 1.25.14, Node 26.12.1, .NET SDK 10.0.404, Java 21.0.13, Android API 36 and an emulator binary. Several tools are installed outside PATH. Rust and an Android NDK were not found in the inspected locations. Windows command interop was not available through PATH; native Windows launch and visual verification require a Windows execution target. These observations are not a complete host inventory.

Music's Windows implementation uses WinUI, `ExtendsContentIntoTitleBar`, `SetTitleBar`, and caption colors. Reuse its native title-bar approach, not a web wrapper.

## 1. Proposed architecture

```text
Windows Storage Browser                Android Storage Browser APK
  C# / WinUI 3                           Kotlin / Compose
          |                                       |
  C# logical client SDK                  Kotlin logical client SDK
          | named pipe                            | Binder / AIDL + file descriptors
          v                                       v
Windows Storage Service                Android Storage Service APK
  C# platform host                        Kotlin platform host
          | C ABI                                 | JNI
          +------------ Rust Storage Core --------+
                         per-device instance
                         SQLite metadata + private content store

Device services <====== authenticated TLS device protocol ======> device services
```

Browser processes never link the repository engine, read its database, handle peer credentials, or choose blob paths. Core code is shared, but runtime instances and repositories are independent on each device. Platform hosts own IPC, OS identities, key custody, lifecycle, networking adapters, and OS integration. Core owns logical operations, invariants, authorization decisions, transactional state, placement planning, and transfer state machines. The exact core/host adapter boundary must be proven before full implementation.

## 2. Technology recommendation and alternatives

- Windows: C# on .NET 10, WinUI 3, a separately running per-user service process, and a C# client library. No LocalSystem privilege requirement for the first release. Installer and startup registration must make the service independently startable and observable.
- Android: Kotlin, Jetpack Compose, a separate service APK and Browser APK, Binder/AIDL, and file-descriptor streaming. The service's sandbox owns the repository; its UI owns pairing and application-grant approval. The Browser requests logical operations only.
- Core: Rust library with a narrow versioned C ABI on Windows and JNI on Android. Use Rust for shared invariants rather than sharing platform process models. This adds Rust/NDK setup and FFI build work; that cost is explicit.
- Persistence: SQLite with foreign keys, migrations, transactions, explicit durability settings, and optimistic entity revisions; private content-addressed SHA-256 blob storage. SQLite's transaction does not include arbitrary filesystem blob writes, so recovery is a first-class subsystem.
- Contracts: versioned JSON Schema for logical request/response records, structured Query AST, event envelopes, and shared conformance fixtures. C# and Kotlin SDKs use native transports. TypeScript is appropriate for contract tooling and a future SDK; it need not run inside either native service.
- Device protocol: separate, versioned control messages and bounded content streams over an established TLS implementation. No custom cryptographic primitives.
- Alternatives: separate C# and Kotlin engines avoid FFI but duplicate the security and integrity implementation. Electron/Tauri would not provide the requested native Windows UI. A cloud coordinator is unnecessary. A shared .NET Android host trades away the preferred native Kotlin service tooling; sharing the core alone is the recommended compromise.

Before committing to Rust, the first implementation gate is a real Windows DLL and Android JNI library executing the same transactional repository test. Failure at that gate requires revisiting this recommendation, not silently forking engines.

## 3. Proposed repository structure

```text
apps/windows/                 WinUI Browser, client SDK, platform service host
apps/android/                 Browser APK, service APK, client SDK
core/                         shared Rust engine and migrations
bindings/                     versioned C ABI and JNI adapters
contracts/                    logical schemas, peer protocol, fixtures
tests/                        shared and cross-process tests
  conformance/                shared behavioral vectors
  integration/                IPC, restart, transfers, two-service scenarios
  acceptance/                 GUI evidence and release checklist
docs/                         architecture, threat model, protocol, decisions
scripts/                      non-Python build and validation tooling
.github/workflows/            one eventual push workflow
```

Only create directories as real code or documentation arrives. No placeholder platform applications. Do not commit or push without authorization. The future push workflow has one build job per platform plus shared validation/final publication if needed; unchanged platform publications retain their verified source identities.

## 4. Core entity relationships

- `StorageNetwork`: stable network ID, owner principal, coordinator device, protocol version.
- `Device`: stable cryptographic identity, platform/capabilities, trust state. Connectivity is observed separately, not a claim a remote caller can assert.
- `DataStore`: stable random ID and medium identity where available, associated with a device. A mount/path is a mutable service-private binding. A missing medium must not silently become a new empty store with the old identity.
- `ApplicationIdentity` and `ApplicationGrant`: OS-bound app identity and independently mutable operation capabilities.
- `Item`: stable ID, name/content type, properties, current-version reference, independent AccessPolicy reference, revision, timestamps, deletion state.
- `ItemVersion`: immutable ID, Item ID, blob hash/length, verified content metadata, creator and creation timestamp. Unique version IDs remain distinct even when content hashes coincide.
- `AccessPolicy`: separate ID, owner, grants, revision. New Items get owner-private policies; owner-private content can replicate to that owner's trusted devices without becoming public.
- `PolicySet`: independent mutable replication, locality, encryption, retention categories; references can target Items, Collections, or Queries. Unsupported requirements return explicit unsupported states, never false compliance.
- `Collection` and `CollectionMembership`: many-to-many membership; deleting a Collection never deletes its Items.
- `SavedQuery`: name, versioned structured condition tree, revision. Query evaluation always intersects with caller authorization.
- `Placement`: version ID, Data Store ID, verification state and evidence timestamp. Device reachability and current availability are separate from last verification.
- `Transfer`: durable resumable work state, expected hashes/length, offsets/chunks, attempts and errors.
- `AuditEvent` and `OutboxEvent`: transactional operation record and durable propagation cursor.

Properties are user-editable metadata. Attributes are generated by the service. Content type validation and hashing happen at ingestion; client-provided values are not blindly promoted to verified attributes.

## 5. Logical application API outline

Each operation receives an authenticated service-side caller context, never a trusted `appId` supplied in a request body.

- Items: create, get, list/search, patch metadata with expected revision, tombstone.
- Uploads: begin, stream, inspect progress, commit, cancel. Commit supports new Item or new immutable ItemVersion with expected Item revision.
- Versions: list, get metadata, read content by version ID. Historical reads evaluate the Item's current AccessPolicy.
- Properties/Attributes: patch properties with revision; read service-generated attributes.
- Access: read/update policy with expected policy revision; list audit changes.
- Policies: read/update desired state; inspect satisfaction and reasons for degradation.
- Collections: create/update/delete, add/remove membership, list contents, manage policies.
- Queries: create/update/delete, execute structured filters, save/name definitions.
- Locality: pin/unpin Item, Collection or Query results to a device ID, never a Data Store/path. Define precedence as the union of required localities plus the strongest required replica count. Conflicts are unsatisfied requirements, not silent weakening.
- Devices: list registered identities and observed connectivity/capabilities.
- Diagnostics: enumerate store health, transfer queue, actual placement and service status, with separate administrative capabilities.
- Events: subscribe from durable cursor; bounded replay; explicit resnapshot-required result after cursor expiry. Delivery is at least once and consumers deduplicate event IDs.

All mutable entities use revision preconditions. Request IDs provide mutation idempotency. Errors distinguish denied, missing, stale revision, offline authority, unsupported policy, unavailable content, invalid definition and integrity failure. Listings use bounded pages. Large content never rides inside JSON or Binder transactions.

## 6. Process and application authorization

### Windows

Use explicitly secured, local-only named pipes; reject remote clients. Pipe ACLs are an OS-user boundary, not sufficient app authorization. Verify connection-derived process/token/package identity and bind it to a user-approved enrollment and capabilities. Prefer packaged application identity for the first client. Do not treat executable paths, request-supplied names, or possession of the OS user account as independent application identity. Specify enrollment/update identity continuity and test spoofing before exposing third-party registration. Unpackaged third-party applications are unsupported until a credible identity mechanism is implemented.

A per-user full-trust Windows process cannot be presented as protection against an administrator or every malicious process running with the same user's unrestricted powers. The threat model must state this limitation.

### Android

Capture Binder calling UID before switching threads or clearing identity; resolve package/signing identity and consult stored grants on every operation. Never accept a package name alone as authentication. Separate APKs do not use shared UID or shared repository files. Export only the deliberate IPC surface; service-owned user consent establishes app grants. Descriptor-based transfers remain bound to caller identity and authorization. Service-owned approval UI cannot be replaced by the requesting app's consent assertion.

The service is bound while clients are active, persists durable jobs, and uses appropriate scheduled work and user-visible foreground transfer execution. Do not promise a permanently running unrestricted Android daemon or instant replication while Android has suspended it. The foreground two-device demonstration is the initial timing guarantee.

## 7. Pairing, communication and authority

### Pairing

1. Windows service creates a short-lived single-use offer, shown by its trusted UI as a QR code.
2. QR includes protocol/network identity, connection hints, a pinned service public-key fingerprint, expiry and a high-entropy bootstrap secret. Connection hints are not exposed in the application Storage API.
3. Android service generates its own device key and connects to the pinned Windows endpoint.
4. Both trusted UIs show device identity and a matching confirmation value. Windows explicitly approves the new device; possessing a photographed QR alone does not complete trust enrollment.
5. Persist reciprocal trust records only after confirmation. Invalidate the offer, exchange capabilities, and switch to mutually authenticated device communication. Support cancellation, replay rejection and interrupted-pairing cleanup.

Keys remain in service-owned platform-protected storage. Revoking a device removes its protocol access; deleting already exported plaintext is not promised. Discovery uses authenticated connection/heartbeat observations, LAN hints and explicit fallback connection hints. LAN multicast never establishes trust. No cross-NAT availability or cloud relay is promised for release one.

### Metadata authority proposal

One selected Windows device coordinates network metadata for release one. Both clients use the same API; service forwarding hides the coordinator from applications. The coordinator serializes accepted mutations, checks revisions, records an ordered outbox, and peers replay durable idempotent changes. No automatic coordinator promotion or consensus is claimed.

With the coordinator unavailable, verified pinned content is intended to remain readable subject to the offline authorization decision below. New uploads may stage durably as pending operations, not appear as globally committed Items. Metadata edits and policy changes do not pretend to commit offline. Pending operations are reauthorized on replay and stale edits return conflicts. This is a proposed availability limitation requiring agreement before implementation.

### Replication

Plan against desired count, distinct devices and required locality; place verified version replicas without application-chosen stores. Transfer into private staging, checkpoint progress, verify final SHA-256 and length, atomically publish the blob, then transactionally publish complete metadata/placement plus outbox event. Never expose staging content as complete. Retry uses existing verified blobs and durable transfer state; hash presence does not grant access. Query and Collection locality reconciliation is durable and reacts to membership/metadata changes.

Startup recovery handles filesystem publication succeeding before metadata commit (recoverable orphan), never the reverse (complete metadata referencing missing content). Garbage collection is conservative, reference-aware and separated from publication. Crash tests must cover every boundary. Report policy satisfaction separately from historical replica verification and present reachability; an offline replica is not automatically lost, and an online device is not proof of correct bytes.

## 8. Integrity, access revocation, and explicit decision gate

A disconnected device cannot learn a revocation that happened elsewhere. Consequently these two promises cannot both be unconditional:

- always read pinned content while disconnected;
- immediately enforce a new remote AccessPolicy against every read everywhere.

Do not disguise this as a cache implementation detail. Obtain approval for a defined offline-access model before implementing it. Options are strict online authorization (fails closed offline, compromising offline pin reads) or explicitly authorized offline reads with acknowledged revocation propagation delay. Do not introduce time-based pinning as a workaround. On connected services, policy updates invalidate authorization state and reads check current policy revisions, including historical versions. Transfer streams require defined revocation checkpoints; previously delivered bytes and external file exports cannot be recalled.

The suggested product choice is owner-authorized offline pin reads with clearly disclosed delayed remote revocation, but this is a proposed amendment/clarification of the brief, not an accepted shortcut.

## 9. Implementation roadmap and acceptance gates

0. Approve stack, single-coordinator/offline-write limitation, and offline authorization semantics. Prove Windows C ABI and Android JNI repository integration. Pin tested tool/dependency versions, not floating latest versions.
1. Local Windows vertical slice: shared engine, migrations, identity, one Data Store, transactional import/versioning, secure local client, real WinUI drag/drop, browsing, preview and export. Verify actual GUI and restart persistence.
2. Android local slice: dedicated service APK, authenticated Binder SDK, native Browser, file picker/import/export, collections and offline repository restart tests. Verify on an owned emulator and then a physical phone.
3. Network slice: native QR pairing and confirmations, TLS peer identity, durable metadata propagation, verified bidirectional transfer, placements, interrupted-transfer retry. Demonstrate both directions using the GUIs.
4. Abstractions: full inspectors, properties/attributes, collections, structured Queries, independent AccessPolicies, replication/locality policies and Item/Collection/Query pinning.
5. Hardening: denied-app tests; current/historical revocation tests under the approved model; concurrent revisions; corrupt/interrupted transfer and crash recovery; offline/online and service lifecycle tests; real Windows and phone demonstration. Settings include honest update checking states; no fake update feed.

Milestones are complete only with their stated native runtime evidence. A Linux test, Windows cross-build, emulator test, installer build and physical-device demonstration are separate gates. This document records the architecture decision, not current implementation completion; see the root README and component validation records for present evidence.

## Reference notes

Platform references retrieved during preparation:

1. Microsoft, WinUI 3 — `https://learn.microsoft.com/en-us/windows/apps/winui/winui3/`
2. Microsoft, Named Pipe Security and Access Rights — `https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights`
3. Android Developers, AIDL — `https://developer.android.com/develop/background-work/services/aidl`
4. Android Developers, Binder API reference — `https://developer.android.com/reference/android/os/Binder`
5. Android Developers, Foreground service timeouts — `https://developer.android.com/develop/background-work/services/fgs/timeout`
6. SQLite, Atomic Commit — `https://sqlite.org/atomiccommit.html`
7. SQLite, Isolation — `https://sqlite.org/isolation.html`

These support platform feasibility and constraints, not the product-specific design choices. WinUI selection is informed by [1]; explicit named-pipe security by [2]; AIDL transport and caller-derived identity by [3][4]; Android lifecycle limitations by [5]; database transaction isolation and recovery considerations by [6][7]. All architectural decisions remain proposals.
