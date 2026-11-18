use serde_json::{json, Value};
use std::{fs, path::PathBuf};
#[test]
fn c_abi_roundtrip_invalid_utf8_null_and_free() {
    use std::ffi::{CStr, CString};
    let r = Repo::new();
    let root = CString::new(r.0.to_str().unwrap()).unwrap();
    let actor =
        CString::new(r#"{"principalId":"owner","capabilities":["readDiagnostics"]}"#).unwrap();
    let command = CString::new(r#"{"op":"health","args":{}}"#).unwrap();
    unsafe {
        let response =
            storage_core::storage_execute(root.as_ptr(), actor.as_ptr(), command.as_ptr());
        assert!(!response.is_null());
        let v: Value = serde_json::from_str(CStr::from_ptr(response).to_str().unwrap()).unwrap();
        assert_eq!(v["ok"], true);
        storage_core::storage_free(response);
        let invalid = [255u8, 0];
        for bad in [std::ptr::null(), invalid.as_ptr().cast()] {
            let response = storage_core::storage_execute(bad, actor.as_ptr(), command.as_ptr());
            assert!(!response.is_null());
            let v: Value =
                serde_json::from_str(CStr::from_ptr(response).to_str().unwrap()).unwrap();
            error(v, "InvalidArgument");
            storage_core::storage_free(response);
        }
        storage_core::storage_free(std::ptr::null_mut());
    }
}
struct Repo(PathBuf);
impl Repo {
    fn new() -> Self {
        let p = std::env::temp_dir().join(format!("storage-test-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&p).unwrap();
        Self(p)
    }
    fn call(&self, principal: &str, caps: &[&str], op: &str, args: Value) -> Value {
        storage_core::execute(
            self.0.to_str().unwrap(),
            &json!({"principalId":principal,"capabilities":caps}).to_string(),
            &json!({"op":op,"args":args}).to_string(),
        )
    }
    fn owner(&self, op: &str, args: Value) -> Value {
        self.call(
            "owner",
            &[
                "readItems",
                "createItems",
                "createVersions",
                "modifyItems",
                "manageAccess",
                "manageCollections",
                "readDiagnostics",
            ],
            op,
            args,
        )
    }
    fn stage(&self, bytes: &[u8]) -> String {
        let p = self.0.join(format!("input-{}", uuid::Uuid::new_v4()));
        fs::write(&p, bytes).unwrap();
        p.to_str().unwrap().to_owned()
    }
}
impl Drop for Repo {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn result(v: Value) -> Value {
    assert_eq!(v["ok"], true, "{v}");
    v["result"].clone()
}
fn error(v: Value, code: &str) {
    assert_eq!(v["ok"], false, "{v}");
    assert_eq!(v["error"]["code"], code, "{v}");
}
#[test]
fn import_reads_real_blob_and_is_owner_private() {
    let r = Repo::new();
    let source = r.stage(b"hello storage");
    let item = result(r.owner(
        "createItem",
        json!({"name":"hello","contentType":"text/plain","sourcePath":source}),
    ));
    assert_eq!(item["revision"], 1);
    assert_eq!(item["attributes"]["byteLength"], 13);
    assert_eq!(item["properties"], json!({}));
    let listed = result(r.owner("listItems", json!({})));
    assert_eq!(listed["items"], json!([item.clone()]));
    assert_eq!(
        result(r.owner("getItem", json!({"itemId":item["id"]}))),
        item
    );
    let read = result(r.owner("readVersion", json!({"versionId":item["currentVersionId"]})));
    assert_eq!(
        fs::read(read["path"].as_str().unwrap()).unwrap(),
        b"hello storage"
    );
    assert_eq!(read["version"]["sha256"], item["attributes"]["sha256"]);
    assert!(
        result(r.call("other", &["readItems"], "listItems", json!({})))["items"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    error(
        r.call(
            "other",
            &["readItems"],
            "readVersion",
            json!({"versionId":item["currentVersionId"]}),
        ),
        "PermissionDenied",
    );
    error(
        r.call("owner", &[], "getItem", json!({"itemId":item["id"]})),
        "PermissionDenied",
    );
    assert!(r.0.join("metadata.sqlite3").is_file());
}
#[test]
fn versions_are_immutable_and_mutations_require_matching_revision() {
    let r = Repo::new();
    let source = r.stage(b"old");
    let first = result(r.owner(
        "createItem",
        json!({"name":"original","contentType":"text/plain","sourcePath":source}),
    ));
    let new_source = r.stage(b"new");
    let second = result(r.owner(
        "createVersion",
        json!({"itemId":first["id"],"expectedRevision":1,"sourcePath":new_source}),
    ));
    assert_eq!(second["revision"], 2);
    assert_ne!(first["currentVersionId"], second["currentVersionId"]);
    error(
        r.owner(
            "createVersion",
            json!({"itemId":first["id"],"expectedRevision":1,"sourcePath":new_source}),
        ),
        "RevisionConflict",
    );
    let third = result(r.owner(
        "createVersion",
        json!({"itemId":first["id"],"expectedRevision":2,"sourcePath":new_source}),
    ));
    assert_ne!(third["currentVersionId"], second["currentVersionId"]);
    assert_eq!(third["attributes"], second["attributes"]);
    let versions = result(r.owner("listVersions", json!({"itemId":first["id"]})));
    assert_eq!(versions["versions"].as_array().unwrap().len(), 3);
    let old = result(r.owner(
        "readVersion",
        json!({"versionId":first["currentVersionId"]}),
    ));
    assert_eq!(fs::read(old["path"].as_str().unwrap()).unwrap(), b"old");
    error(
        r.owner(
            "renameItem",
            json!({"itemId":first["id"],"expectedRevision":2,"name":"stale"}),
        ),
        "RevisionConflict",
    );
    let renamed = result(r.owner(
        "renameItem",
        json!({"itemId":first["id"],"expectedRevision":3,"name":"renamed"}),
    ));
    assert_eq!(renamed["revision"], 4);
    assert_eq!(renamed["name"], "renamed");
    assert_eq!(renamed["currentVersionId"], third["currentVersionId"]);
}
#[test]
fn policy_revisions_are_independent_and_revoke_historical_reads() {
    let r = Repo::new();
    let item = result(r.owner(
        "createItem",
        json!({"name":"private","contentType":"text/plain","sourcePath":r.stage(b"v1")}),
    ));
    let next = result(r.owner(
        "createVersion",
        json!({"itemId":item["id"],"expectedRevision":1,"sourcePath":r.stage(b"v2")}),
    ));
    let policy = result(r.owner(
        "setAccessPolicy",
        json!({"itemId":item["id"],"expectedRevision":1,"readers":["reader"]}),
    ));
    assert_eq!(policy["revision"], 2);
    assert_eq!(policy["ownerPrincipalId"], "owner");
    assert_eq!(policy["id"], item["accessPolicyId"]);
    let after = result(r.owner("getItem", json!({"itemId":item["id"]})));
    assert_eq!(after["revision"], 2);
    assert_eq!(after["modifiedAt"], next["modifiedAt"]);
    assert_eq!(after["accessPolicyRevision"], 2);
    result(r.call(
        "reader",
        &["readItems"],
        "readVersion",
        json!({"versionId":item["currentVersionId"]}),
    ));
    error(
        r.call(
            "reader",
            &[],
            "readVersion",
            json!({"versionId":item["currentVersionId"]}),
        ),
        "PermissionDenied",
    );
    error(
        r.call(
            "reader",
            &["modifyItems"],
            "renameItem",
            json!({"itemId":item["id"],"expectedRevision":2,"name":"takeover"}),
        ),
        "PermissionDenied",
    );
    error(
        r.call(
            "reader",
            &["manageAccess"],
            "setAccessPolicy",
            json!({"itemId":item["id"],"expectedRevision":2,"readers":[]}),
        ),
        "PermissionDenied",
    );
    error(
        r.owner(
            "setAccessPolicy",
            json!({"itemId":item["id"],"expectedRevision":1,"readers":[]}),
        ),
        "RevisionConflict",
    );
    result(r.owner(
        "setAccessPolicy",
        json!({"itemId":item["id"],"expectedRevision":2,"readers":[]}),
    ));
    for version in [&item["currentVersionId"], &next["currentVersionId"]] {
        error(
            r.call(
                "reader",
                &["readItems"],
                "readVersion",
                json!({"versionId":version}),
            ),
            "PermissionDenied",
        );
    }
    result(r.owner("readVersion", json!({"versionId":item["currentVersionId"]})));
    let db = rusqlite::Connection::open(r.0.join("metadata.sqlite3")).unwrap();
    assert_eq!(
        db.query_row("SELECT COUNT(*) FROM audit", [], |row| row.get::<_, i64>(0))
            .unwrap(),
        2
    );
}
#[test]
fn collections_are_private_memberships_not_item_owners() {
    let r = Repo::new();
    let item = result(r.call(
        "alice",
        &["createItems"],
        "createItem",
        json!({"name":"alice item","contentType":"text/plain","sourcePath":r.stage(b"alice")}),
    ));
    result(r.call(
        "alice",
        &["manageAccess"],
        "setAccessPolicy",
        json!({"itemId":item["id"],"expectedRevision":1,"readers":["owner"]}),
    ));
    let c = result(r.owner("createCollection", json!({"name":"links"})));
    assert_eq!(c["revision"], 1);
    let added = result(r.owner(
        "addToCollection",
        json!({"collectionId":c["id"],"itemId":item["id"],"expectedRevision":1}),
    ));
    assert_eq!(added["revision"], 2);
    error(
        r.owner(
            "removeFromCollection",
            json!({"collectionId":c["id"],"itemId":item["id"],"expectedRevision":1}),
        ),
        "RevisionConflict",
    );
    assert_eq!(
        result(r.owner("listCollections", json!({})))["collections"],
        json!([added])
    );
    assert_eq!(
        result(r.owner("listCollectionItems", json!({"collectionId":c["id"]})))["items"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    assert_eq!(
        result(r.call("alice", &["readItems"], "listCollections", json!({})))["collections"],
        json!([])
    );
    error(
        r.call(
            "alice",
            &["readItems"],
            "listCollectionItems",
            json!({"collectionId":c["id"]}),
        ),
        "PermissionDenied",
    );
    result(r.call(
        "alice",
        &["manageAccess"],
        "setAccessPolicy",
        json!({"itemId":item["id"],"expectedRevision":2,"readers":[]}),
    ));
    assert_eq!(
        result(r.owner("listCollectionItems", json!({"collectionId":c["id"]}))),
        json!({"items":[],"hasMore":false})
    );
    let removed = result(r.owner(
        "removeFromCollection",
        json!({"collectionId":c["id"],"itemId":item["id"],"expectedRevision":2}),
    ));
    assert_eq!(removed["revision"], 3);
    result(r.owner(
        "deleteCollection",
        json!({"collectionId":c["id"],"expectedRevision":3}),
    ));
    assert_eq!(
        result(r.call(
            "alice",
            &["readItems"],
            "getItem",
            json!({"itemId":item["id"]})
        ))["revision"],
        1
    );
    assert_eq!(result(r.owner("health", json!({})))["versionCount"], 1);
}
#[test]
fn corruption_and_missing_blobs_fail_closed_including_deduplication() {
    let r = Repo::new();
    let source = r.stage(b"verified content");
    let item = result(r.owner(
        "createItem",
        json!({"name":"blob","contentType":"text/plain","sourcePath":source}),
    ));
    let read = result(r.owner("readVersion", json!({"versionId":item["currentVersionId"]})));
    let path = read["path"].as_str().unwrap();
    fs::write(path, b"corrupted bytes!").unwrap();
    error(
        r.owner("readVersion", json!({"versionId":item["currentVersionId"]})),
        "IntegrityError",
    );
    error(
        r.owner(
            "createItem",
            json!({"name":"reuse","contentType":"text/plain","sourcePath":source}),
        ),
        "IntegrityError",
    );
    error(
        r.owner(
            "createVersion",
            json!({"itemId":item["id"],"expectedRevision":1,"sourcePath":source}),
        ),
        "IntegrityError",
    );
    assert_eq!(result(r.owner("health", json!({})))["itemCount"], 1);
    assert_eq!(result(r.owner("health", json!({})))["versionCount"], 1);
    assert_eq!(fs::read(path).unwrap(), b"corrupted bytes!");
    fs::remove_file(path).unwrap();
    error(
        r.owner("readVersion", json!({"versionId":item["currentVersionId"]})),
        "IntegrityError",
    );
}

#[test]
fn failed_metadata_commit_rolls_back_and_keeps_safe_orphan() {
    let r = Repo::new();
    result(r.owner("health", json!({})));
    let db = rusqlite::Connection::open(r.0.join("metadata.sqlite3")).unwrap();
    db.execute_batch("CREATE TRIGGER fail_version BEFORE INSERT ON versions BEGIN SELECT RAISE(ABORT,'injected metadata failure'); END;").unwrap();
    error(r.owner("createItem", json!({"name":"never visible","contentType":"text/plain","sourcePath":r.stage(b"complete orphan")})), "InternalError");
    assert_eq!(result(r.owner("listItems", json!({})))["items"], json!([]));
    assert_eq!(result(r.owner("health", json!({})))["versionCount"], 0);
    assert_eq!(
        db.query_row("SELECT COUNT(*) FROM policies", [], |row| row
            .get::<_, i64>(0))
            .unwrap(),
        0
    );
    let blobs: Vec<_> = fs::read_dir(r.0.join("blobs")).unwrap().collect();
    assert_eq!(blobs.len(), 1);
    assert_eq!(
        fs::read(blobs[0].as_ref().unwrap().path()).unwrap(),
        b"complete orphan"
    );
    fs::write(r.0.join("staging").join("interrupted-stage"), b"partial").unwrap();
    result(r.owner("health", json!({})));
    assert!(r.0.join("staging").join("interrupted-stage").exists());
    assert_eq!(result(r.owner("listItems", json!({})))["items"], json!([]));
    db.execute_batch("DROP TRIGGER fail_version").unwrap();
    result(r.owner(
        "createItem",
        json!({"name":"valid","contentType":"text/plain","sourcePath":r.stage(b"complete orphan")}),
    ));
    assert_eq!(fs::read_dir(r.0.join("blobs")).unwrap().count(), 1);
}

#[test]
fn competing_revisions_have_exactly_one_winner() {
    let r = Repo::new();
    let item = result(r.owner(
        "createItem",
        json!({"name":"initial","contentType":"text/plain","sourcePath":r.stage(b"data")}),
    ));
    let barrier = std::sync::Arc::new(std::sync::Barrier::new(3));
    let mut threads = Vec::new();
    for name in ["left", "right"] {
        let root = r.0.to_str().unwrap().to_owned();
        let barrier = barrier.clone();
        let command = json!({"op":"renameItem","args":{"itemId":item["id"],"expectedRevision":1,"name":name}}).to_string();
        threads.push(std::thread::spawn(move || {
            barrier.wait();
            storage_core::execute(
                &root,
                r#"{"principalId":"owner","capabilities":["modifyItems"]}"#,
                &command,
            )
        }));
    }
    barrier.wait();
    let replies: Vec<_> = threads.into_iter().map(|t| t.join().unwrap()).collect();
    assert_eq!(replies.iter().filter(|v| v["ok"] == true).count(), 1);
    error(
        replies.into_iter().find(|v| v["ok"] == false).unwrap(),
        "RevisionConflict",
    );
    assert_eq!(
        result(r.owner("getItem", json!({"itemId":item["id"]})))["revision"],
        2
    );
}

#[test]
fn newer_schema_is_rejected_without_modifying_its_version() {
    let r = Repo::new();
    let db = rusqlite::Connection::open(r.0.join("metadata.sqlite3")).unwrap();
    db.pragma_update(None, "user_version", 99).unwrap();
    error(r.owner("health", json!({})), "InternalError");
    assert_eq!(
        db.query_row("PRAGMA user_version", [], |row| row.get::<_, i64>(0))
            .unwrap(),
        99
    );
}

#[test]
fn all_operations_deny_missing_capabilities_and_invalid_json_is_structured() {
    let r = Repo::new();
    for op in [
        "health",
        "listItems",
        "createItem",
        "getItem",
        "createVersion",
        "listVersions",
        "readVersion",
        "renameItem",
        "setAccessPolicy",
        "createCollection",
        "listCollections",
        "addToCollection",
        "removeFromCollection",
        "deleteCollection",
        "listCollectionItems",
    ] {
        error(r.call("owner", &[], op, json!({})), "PermissionDenied");
    }
    error(
        storage_core::execute(r.0.to_str().unwrap(), "not JSON", "{}"),
        "InvalidArgument",
    );
    error(
        storage_core::execute(
            r.0.to_str().unwrap(),
            r#"{"principalId":"owner","capabilities":["readItems"]}"#,
            "not JSON",
        ),
        "InvalidArgument",
    );
    error(
        storage_core::execute(
            r.0.to_str().unwrap(),
            r#"{"principalId":"owner"}"#,
            r#"{"op":"listItems","args":{}}"#,
        ),
        "PermissionDenied",
    );
    error(
        r.owner("unimplementedNetworkOperation", json!({})),
        "UnsupportedOperation",
    );
    assert_eq!(result(r.owner("health", json!({})))["itemCount"], 0);
}

#[test]
fn item_listing_is_bounded_and_reports_truncation() {
    let r = Repo::new();
    let source = r.stage(&[0, 255, 128, 13, 10]);
    let collection = result(r.owner("createCollection", json!({"name":"short members"})));
    for index in 0..501 {
        let item = result(r.owner("createItem", json!({"name":format!("item {index}"),"contentType":"application/octet-stream","sourcePath":source})));
        result(r.owner(
            "addToCollection",
            json!({"collectionId":collection["id"],"itemId":item["id"],"expectedRevision":index+1}),
        ));
        if index == 499 {
            for (op, args) in [
                ("listItems", json!({})),
                (
                    "listCollectionItems",
                    json!({"collectionId":collection["id"]}),
                ),
            ] {
                let full = result(r.owner(op, args));
                assert_eq!(full["items"].as_array().unwrap().len(), 500);
                assert_eq!(full["hasMore"], false);
            }
        }
    }
    let list = result(r.owner("listItems", json!({})));
    assert_eq!(list["items"].as_array().unwrap().len(), 500);
    assert_eq!(list["hasMore"], true);
    let members = result(r.owner(
        "listCollectionItems",
        json!({"collectionId":collection["id"]}),
    ));
    assert_eq!(members["items"].as_array().unwrap().len(), 500);
    assert_eq!(members["hasMore"], true);
    assert_eq!(result(r.owner("health", json!({})))["itemCount"], 501);
    assert_eq!(fs::read_dir(r.0.join("blobs")).unwrap().count(), 1);
}

#[test]
fn item_listing_bounds_serialized_bytes_for_maximum_host_metadata() {
    let r = Repo::new();
    let source = r.stage(b"large metadata");
    // Windows accepts 1024 UTF-16 units per logical string (Android is stricter).
    // Mix multibyte UTF-8 and JSON escapes; counting characters is not sufficient.
    let name = "界\"".repeat(512);
    let content_type = format!("application/{}", "a".repeat(1012));
    assert_eq!(name.encode_utf16().count(), 1024);
    assert_eq!(content_type.len(), 1024);
    for _ in 0..500 {
        result(r.owner(
            "createItem",
            json!({"name":name,"contentType":content_type,"sourcePath":source}),
        ));
    }
    let response = r.owner("listItems", json!({}));
    let bytes = serde_json::to_vec(&response).unwrap().len();
    assert!(
        bytes <= 400 * 1024,
        "complete listItems envelope is {bytes} bytes"
    );
    let listed = result(response);
    let items = listed["items"].as_array().unwrap();
    assert!(!items.is_empty());
    assert!(items.len() < 500);
    assert_eq!(listed["hasMore"], true);
    assert!(items
        .iter()
        .all(|item| item["name"] == name && item["contentType"] == content_type));
    let hidden = result(r.call("stranger", &["readItems"], "listItems", json!({})));
    assert_eq!(hidden, json!({"items":[],"hasMore":false}));
}

#[test]
fn collection_item_listing_bounds_serialized_bytes_for_android_metadata() {
    let r = Repo::new();
    let source = r.stage(b"collection metadata");
    let collection = result(r.owner("createCollection", json!({"name":"bounded"})));
    // Both strings are valid at Android's exact UTF-16 length limits. JSON
    // expands each non-NUL control character in the name to six ASCII bytes.
    let name = "\u{1}".repeat(512);
    let content_type = format!("application/{}", "a".repeat(244));
    for index in 0..500 {
        let item = result(r.owner(
            "createItem",
            json!({"name":name,"contentType":content_type,"sourcePath":source}),
        ));
        result(r.owner(
            "addToCollection",
            json!({"collectionId":collection["id"],"itemId":item["id"],"expectedRevision":index+1}),
        ));
    }
    let response = r.owner(
        "listCollectionItems",
        json!({"collectionId":collection["id"]}),
    );
    let bytes = serde_json::to_vec(&response).unwrap().len();
    assert!(
        bytes <= 400 * 1024,
        "complete listCollectionItems envelope is {bytes} bytes"
    );
    let listed = result(response);
    let items = listed["items"].as_array().unwrap();
    assert!(!items.is_empty());
    assert!(items.len() < 500);
    assert_eq!(listed["hasMore"], true);
    assert!(items
        .iter()
        .all(|item| item["name"] == name && item["contentType"] == content_type));
    error(
        r.call(
            "stranger",
            &["readItems"],
            "listCollectionItems",
            json!({"collectionId":collection["id"]}),
        ),
        "PermissionDenied",
    );
}

#[test]
fn version_listing_preserves_500_records_and_reports_authorized_truncation() {
    let r = Repo::new();
    let source = r.stage(b"version bytes");
    let item = result(r.owner(
        "createItem",
        json!({"name":"history","contentType":"text/plain","sourcePath":source}),
    ));
    for revision in 1..500 {
        result(r.owner(
            "createVersion",
            json!({"itemId":item["id"],"expectedRevision":revision,"sourcePath":source}),
        ));
    }
    let full = result(r.owner("listVersions", json!({"itemId":item["id"]})));
    result(r.owner(
        "createVersion",
        json!({"itemId":item["id"],"expectedRevision":500,"sourcePath":source}),
    ));
    let response = r.owner("listVersions", json!({"itemId":item["id"]}));
    assert!(serde_json::to_vec(&response).unwrap().len() <= 400 * 1024);
    let truncated = result(response);
    assert_eq!(truncated["versions"].as_array().unwrap().len(), 500);
    assert_eq!(truncated["hasMore"], true);
    assert_eq!(full["versions"].as_array().unwrap().len(), 500);
    assert_eq!(full["hasMore"], false);
    error(
        r.call(
            "stranger",
            &["readItems"],
            "listVersions",
            json!({"itemId":item["id"]}),
        ),
        "PermissionDenied",
    );
}

#[test]
fn collection_listing_bounds_counts_bytes_and_private_truncation() {
    for name in ["short".to_owned(), "\"".repeat(1024)] {
        let r = Repo::new();
        for _ in 0..500 {
            result(r.owner("createCollection", json!({"name":name})));
        }
        let full = result(r.owner("listCollections", json!({})));
        result(r.owner("createCollection", json!({"name":name})));
        let response = r.owner("listCollections", json!({}));
        let bytes = serde_json::to_vec(&response).unwrap().len();
        assert!(
            bytes <= 400 * 1024,
            "complete listCollections envelope is {bytes} bytes"
        );
        let listed = result(response);
        let collections = listed["collections"].as_array().unwrap();
        if name == "short" {
            assert_eq!(collections.len(), 500);
            assert_eq!(full["collections"].as_array().unwrap().len(), 500);
            assert_eq!(full["hasMore"], false);
        } else {
            assert!(!collections.is_empty());
            assert!(collections.len() < 500);
            assert!(collections
                .iter()
                .all(|collection| collection["name"] == name));
        }
        assert_eq!(listed["hasMore"], true);
        assert_eq!(
            result(r.call("stranger", &["readItems"], "listCollections", json!({}))),
            json!({"collections":[],"hasMore":false})
        );
    }
}

#[test]
fn oversized_single_record_is_not_emitted_or_silently_skipped() {
    let r = Repo::new();
    let source = r.stage(b"oversized logical metadata");
    // The private core ABI does not impose the public hosts' string limits.
    // A record that cannot fit must still never overflow a listing response.
    let item = result(r.owner(
        "createItem",
        json!({"name":"x".repeat(400 * 1024),"contentType":"text/plain","sourcePath":source}),
    ));
    let collection = result(r.owner("createCollection", json!({"name":"x".repeat(400 * 1024)})));
    result(r.owner(
        "addToCollection",
        json!({"collectionId":collection["id"],"itemId":item["id"],"expectedRevision":1}),
    ));
    for (op, args, field) in [
        ("listItems", json!({}), "items"),
        (
            "listCollectionItems",
            json!({"collectionId":collection["id"]}),
            "items",
        ),
        ("listCollections", json!({}), "collections"),
    ] {
        let response = r.owner(op, args);
        assert!(serde_json::to_vec(&response).unwrap().len() <= 400 * 1024);
        let listed = result(response);
        assert_eq!(listed[field], json!([]));
        assert_eq!(listed["hasMore"], true);
    }
}

#[test]
fn identity_survives_reopen_and_diagnostics_requires_capability() {
    let r = Repo::new();
    let a = result(r.owner("health", json!({})));
    let b = result(r.owner("health", json!({})));
    assert_eq!(a, b);
    assert_eq!(a["itemCount"], 0);
    assert_eq!(a["status"], "healthy");
    assert!(uuid::Uuid::parse_str(a["deviceId"].as_str().unwrap()).is_ok());
    error(
        r.call("owner", &[], "health", json!({})),
        "PermissionDenied",
    );
}
