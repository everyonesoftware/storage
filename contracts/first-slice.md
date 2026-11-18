# First vertical slice: frozen integration surface

Status: approved architecture; this document fixes the initial cross-worker integration surface. Local storage only in this slice. Do not claim device pairing, replication, or global metadata authority is implemented.

## Internal core ABI (service use only)

Library name `storage_core`; Rust workspace crate `storage-core` in `core/`.

```c
char *storage_execute(const char *repository_root_utf8,
                      const char *trusted_actor_json_utf8,
                      const char *command_json_utf8);
void storage_free(char *response);
```

The caller copies and frees the response. Null/invalid inputs return an error envelope without unwinding across FFI. Commands execute against the service-owned root. This is NOT the public application wire protocol: filesystem paths and trusted actor identities must never be accepted from client command JSON. Platform hosts construct them after authenticating callers. Large content crosses public IPC as streams/file descriptors, never base64 JSON.

Android JNI entry point: `com.everyonesoftware.storage.service.NativeCore.execute(root: String, actorJson: String, commandJson: String): String`, a static native method. Loads `storage_core`. Same implementation and response envelope as C ABI.

Trusted actor example: `{"principalId":"local-owner","capabilities":["readItems","createItems","createVersions","modifyItems","manageAccess","manageCollections","executeQueries","readDiagnostics"]}`. Missing capabilities deny. Initial platform hosts use the same local owner principal; this is not a claim of established distributed owner identity. App identity is separately authenticated by the host.

Request: `{"op":"listItems","args":{}}`.
Success: `{"ok":true,"result":...}`.
Error: `{"ok":false,"error":{"code":"PermissionDenied","message":"..."}}`.

## Operations

- `health {}` -> `{deviceId,dataStoreId,itemCount,versionCount,status:"healthy"}`. Stable IDs survive reopening.
- `listItems {}` -> `{items:[Item]}` (bounded to 500 for this local slice; `hasMore` explicitly indicates truncation if implemented).
- `createItem {name,contentType,sourcePath}` -> `Item`. `sourcePath` is a service-private staged file, never a client-supplied path.
- `getItem {itemId}` -> `Item`.
- `createVersion {itemId,expectedRevision,sourcePath}` -> `Item`.
- `listVersions {itemId}` -> `{versions:[ItemVersion]}`.
- `readVersion {versionId}` -> `{path,version:ItemVersion}`. Service-only internal resolution after authorization; public host streams/copies bytes, never returns the path. Verify content before resolving as healthy.
- `renameItem {itemId,expectedRevision,name}` -> `Item`.
- `setAccessPolicy {itemId,expectedRevision,readers:[principalId]}` -> `AccessPolicy`. `expectedRevision` is the AccessPolicy revision. Owner retains access; changes are audited and apply to all versions without changing Item revision/content.
- `createCollection {name}` -> `Collection`.
- `listCollections {}` -> `{collections:[Collection]}`.
- `addToCollection {collectionId,itemId,expectedRevision}` -> `Collection`.
- `removeFromCollection {collectionId,itemId,expectedRevision}` -> `Collection`.
- `deleteCollection {collectionId,expectedRevision}` -> `{deleted:true}`; never deletes Items.
- `listCollectionItems {collectionId}` -> `{items:[Item]}`.

## Records

Item: `{id,name,contentType,revision,currentVersionId,accessPolicyId,accessPolicyRevision,createdAt,modifiedAt,attributes:{byteLength,sha256},properties:{}}`. Times are RFC3339 UTC strings. Default owner-private. Attributes are generated, not caller-editable.

ItemVersion: `{id,itemId,sha256,byteLength,createdAt}`. Unique immutable IDs even for identical content.

AccessPolicy: `{id,revision,ownerPrincipalId,readers:[...]}`.

Collection: `{id,name,revision,createdAt}`. Per-owner private in this slice; item reads remain independently authorized.

Errors: `PermissionDenied`, `NotFound`, `RevisionConflict`, `InvalidArgument`, `IntegrityError`, `IoError`, `InternalError`, `UnsupportedOperation`.

## Acceptance boundaries

Transactional metadata, immutable versions, service-side capability checks AND current Item AccessPolicy checks, hash-verified reads, atomic content publication, revision conflicts, stable store identity, independent AccessPolicy revision and current/historical access checks are required. Recovery must never publish partial content. UI shows real empty, loading, error and data states; no seeded production Items or fabricated transfer/device rows.

Platform public SDKs expose logical import/read/list/version operations. Service IPC must allowlist operations and reconstruct internal commands; never tunnel arbitrary core commands. Diagnostics may expose stable identities/counts, but not repository paths. Networking, saved Queries and policy placement planning may remain visibly not yet implemented. Do not claim full first-release acceptance.
