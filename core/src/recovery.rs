//! Process-exit fault tests. Hooks are compiled only into the Rust unit-test binary.
use super::*;
use std::process::Command;
pub(super) fn crash_point(point: &str) {
    if std::env::var("STORAGE_TEST_CRASH_ROOT").is_ok()
        && std::env::var("STORAGE_TEST_CRASH_POINT").as_deref() == Ok(point)
    {
        std::process::exit(86);
    }
}

#[test]
fn crash_child() {
    let Ok(root) = std::env::var("STORAGE_TEST_CRASH_ROOT") else {
        return;
    };
    let source = Path::new(&root).join("source");
    let response=execute(&root,r#"{"principalId":"owner","capabilities":["createItems"]}"#,&json!({"op":"createItem","args":{"name":"interrupted","contentType":"text/plain","sourcePath":source}}).to_string());
    assert_eq!(response["ok"], true, "{response}");
}

#[test]
fn process_exit_at_publication_boundaries_never_exposes_partial_item() {
    for boundary in ["staged", "published", "metadata", "ready"] {
        let root = std::env::temp_dir().join(format!("storage-crash-{}", id()));
        fs::create_dir_all(&root).unwrap();
        let root_text = root.to_str().unwrap();
        fs::write(root.join("source"), b"complete durable bytes").unwrap();
        let actor = r#"{"principalId":"owner","capabilities":["readDiagnostics","readItems"]}"#;
        let before = execute(root_text, actor, r#"{"op":"health","args":{}}"#);
        let status = Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "recovery::crash_child", "--nocapture"])
            .env("STORAGE_TEST_CRASH_ROOT", root_text)
            .env("STORAGE_TEST_CRASH_POINT", boundary)
            .status()
            .unwrap();
        assert_eq!(status.code(), Some(86), "child did not exit at {boundary}");
        let after = execute(root_text, actor, r#"{"op":"health","args":{}}"#);
        assert_eq!(
            after, before,
            "metadata survived incomplete publication at {boundary}"
        );
        assert_eq!(
            execute(root_text, actor, r#"{"op":"listItems","args":{}}"#)["result"]["items"],
            json!([])
        );
        if boundary == "staged" {
            assert_eq!(fs::read_dir(root.join("blobs")).unwrap().count(), 0);
            assert_eq!(fs::read_dir(root.join("staging")).unwrap().count(), 1);
        } else {
            let paths: Vec<_> = fs::read_dir(root.join("blobs"))
                .unwrap()
                .map(|e| e.unwrap().path())
                .collect();
            assert_eq!(paths.len(), 1);
            assert_eq!(fs::read(&paths[0]).unwrap(), b"complete durable bytes");
        }
        fs::remove_dir_all(root).unwrap();
    }
}
