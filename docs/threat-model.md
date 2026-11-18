# Storage security boundaries: initial local slice

This document specifies the intended threat model and review checklist. It is not evidence that all listed controls have been implemented or tested. See component validation records and `docs/acceptance.md` for actual coverage.

## Assets

- Item bytes, including immutable historical versions.
- Metadata, AccessPolicies, application grants and operation audit records.
- Stable device/Data Store identities and future device trust credentials.
- Integrity of metadata-to-content references.

## Principals and authority

The platform service authenticates application identity using OS-derived information. The core receives a trusted caller context constructed by that service. A public request cannot choose its principal, capabilities, repository root, source path, content path or peer identity. The initial host-local owner principal is not evidence of a paired network owner.

Capabilities and Item authorization are separate: `readItems` is necessary but does not grant read access to every Item. A current AccessPolicy governs every version. Collections and Queries cannot bypass Item access checks. Diagnostics require a separate grant and do not expose paths through public APIs.

## Android

Browser and service are separate APKs and UIDs. Only the service contains the native repository engine and accesses its sandbox. Binder permissions, the connection-derived calling UID, expected package/signing identity and service-owned capability mapping must all remain on the trusted side. Do not authenticate by a request-supplied package name, a claimed PID or a boolean from Browser UI.

Capture caller identity inside an active incoming Binder transaction before moving work to a thread pool. Bound service connection permission alone does not authorize every method. Public descriptor ownership/closure and cancellation must be defined. Imports must stage/copy through service-owned descriptors without accepting an arbitrary filesystem path. Android file picker grants belong to the Browser and content crosses the service boundary explicitly.

Debug APK signing is a development arrangement, not a release identity policy. Third-party enrollment and independently configurable grants require service-owned approval UI before being advertised as supported.

## Windows

Named pipe ACLs restrict OS users but are not sufficient application identity. Reject remote connections. Authenticate the connecting process/package using connection-derived information, and consult an explicit approved app grant. Requests cannot self-identify as Storage Browser. Unpackaged development execution must not silently become the release authorization mechanism.

A full-trust same-user process and a per-user repository are not an absolute isolation boundary against an administrator, debugger/injector, or every malicious process with the owner's unrestricted filesystem/token powers. Describe this limitation rather than promising an OS sandbox that does not exist. Production installer/service ACLs and app identity continuity across updates are separate gates.

## Integrity and concurrency

1. Stage incoming bytes privately; incomplete content is never listed as complete.
2. Hash bytes and durably publish complete content before committing references.
3. Commit metadata, new version, current-version change and audit/outbox state transactionally.
4. Reject stale entity revisions; do not replace an earlier immutable version.
5. Recheck current access on historical reads; do not authorize by possession of a hash/path.
6. Detect missing/corrupt published bytes and fail closed. A matching filename alone proves nothing.
7. Treat orphan blobs conservatively after crashes; never garbage-collect from a partial reference snapshot.
8. Distinguish store health, verified bytes, and reachability; do not report replication health before implementing replication.

The service-owned directory is a trusted filesystem boundary in the initial implementation. A hostile administrator modifying it is out of scope for prevention but accidental or externally introduced content corruption must still be detected on read. Encryption-at-rest policy enforcement is not implemented merely because device communication will use TLS.

## Network slice, not yet available

A QR offer is short-lived, single-use and explicitly confirmed on both trusted device UIs. Public-key pinning precedes credential exchange. Later connections require reciprocal device authentication. Discovery is a connection hint, never identity proof. Paired services need separate replication capabilities; a service cannot self-declare arbitrary owner authority in an unauthenticated message.

The approved initial coordinator is Windows, with no automatic failover. Offline queued mutations must be reauthorized at replay. Offline pinned reads are explicitly authorized with delayed remote revocation; connected services must not intentionally retain stale permission decisions. No system can recall already exported plaintext.

## Required negative tests

- Missing capability and unrelated owner cannot create/read protected content.
- AccessPolicy revocation affects an already-known historical version ID.
- Application request cannot tunnel internal source/path/actor arguments.
- Unapproved Binder/pipe client is denied by the actual service.
- Wrong expected revision leaves prior metadata/content unchanged.
- Corrupt bytes and failed ingestion do not become successful reads/complete Items.
- Deleting a Collection does not delete the underlying Item.
- Restart does not replace stable identities or lose committed versions.

Native enforcement tests and platform launch remain required even when shared-core tests pass.
