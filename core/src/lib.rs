mod blob;
mod collections;
mod ffi;
pub use ffi::{storage_execute, storage_free};
mod items;
mod listing;
use listing::bounded_list;
mod mutations;
#[cfg(test)]
mod recovery;
use rusqlite::{params, Connection, OptionalExtension};
use serde_json::{json, Value};
use std::{
    fs,
    path::{Path, PathBuf},
    time::Duration,
};
use uuid::Uuid;

type Result<T> = std::result::Result<T, Error>;
#[derive(Debug)]
struct Error {
    code: &'static str,
    message: String,
}
impl Error {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}
impl From<rusqlite::Error> for Error {
    fn from(e: rusqlite::Error) -> Self {
        Self::new("InternalError", e.to_string())
    }
}
impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Self::new("IoError", e.to_string())
    }
}
fn text<'a>(v: &'a Value, key: &str) -> Result<&'a str> {
    v.get(key)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| {
            Error::new(
                "InvalidArgument",
                format!("{key} must be a nonempty string"),
            )
        })
}
fn id() -> String {
    Uuid::new_v4().to_string()
}
struct Actor {
    principal: String,
    capabilities: Vec<String>,
}
impl Actor {
    fn parse(s: &str) -> Result<Self> {
        let v: Value = serde_json::from_str(s)
            .map_err(|_| Error::new("InvalidArgument", "invalid actor JSON"))?;
        let principal = text(&v, "principalId")?.to_owned();
        let capabilities = v["capabilities"]
            .as_array()
            .ok_or_else(|| Error::new("PermissionDenied", "missing capabilities"))?
            .iter()
            .map(|x| {
                x.as_str()
                    .map(str::to_owned)
                    .ok_or_else(|| Error::new("InvalidArgument", "invalid capability"))
            })
            .collect::<Result<Vec<_>>>()?;
        Ok(Self {
            principal,
            capabilities,
        })
    }
    fn require(&self, cap: &str) -> Result<()> {
        if self.capabilities.iter().any(|c| c == cap) {
            Ok(())
        } else {
            Err(Error::new("PermissionDenied", "missing capability"))
        }
    }
}
struct Repository {
    db: Connection,
    root: PathBuf,
}
impl Repository {
    fn open(root: &str) -> Result<Self> {
        let root = PathBuf::from(root);
        fs::create_dir_all(root.join("blobs"))?;
        fs::create_dir_all(root.join("staging"))?;
        let db = Connection::open(root.join("metadata.sqlite3"))?;
        db.busy_timeout(Duration::from_secs(10))?;
        let version: i64 = db.query_row("PRAGMA user_version", [], |r| r.get(0))?;
        if version > 1 {
            return Err(Error::new(
                "InternalError",
                "repository schema is newer than this core",
            ));
        }
        db.execute_batch(
            "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;",
        )?;
        if version == 0 {
            db.execute_batch("BEGIN IMMEDIATE")?;
            db.execute_batch(include_str!("../migrations/001_initial.sql"))?;
            for key in ["deviceId", "dataStoreId"] {
                db.execute(
                    "INSERT OR IGNORE INTO identity(key,value) VALUES (?1,?2)",
                    params![key, id()],
                )?;
            }
            db.execute_batch("PRAGMA user_version=1; COMMIT")?;
        }
        Ok(Self { db, root })
    }
    fn dispatch(&mut self, actor: &Actor, op: &str, args: &Value) -> Result<Value> {
        match op {
            "createItem" => self.create_item(actor, args),
            "createVersion" => self.create_version(actor, args),
            "listVersions" => self.list_versions(actor, args),
            "renameItem" => self.rename(actor, args),
            "setAccessPolicy" => self.set_access(actor, args),
            "listItems" => self.list_items(actor),
            "createCollection"
            | "listCollections"
            | "addToCollection"
            | "removeFromCollection"
            | "deleteCollection"
            | "listCollectionItems" => self.collections(actor, op, args),
            "getItem" => {
                actor.require("readItems")?;
                let item = text(args, "itemId")?;
                self.authorize(actor, item, false)?;
                self.item(item)
            }
            "readVersion" => {
                actor.require("readItems")?;
                let version = self.version(text(args, "versionId")?)?;
                self.authorize(actor, text(&version, "itemId")?, false)?;
                let path = self.root.join("blobs").join(text(&version, "sha256")?);
                blob::verify(
                    &path,
                    text(&version, "sha256")?,
                    version["byteLength"]
                        .as_u64()
                        .ok_or_else(|| Error::new("IntegrityError", "invalid length"))?,
                )?;
                Ok(json!({"path":path,"version":version}))
            }
            "health" => {
                actor.require("readDiagnostics")?;
                let identity = |key| {
                    self.db
                        .query_row("SELECT value FROM identity WHERE key=?1", [key], |r| {
                            r.get::<_, String>(0)
                        })
                };
                let items: i64 = self
                    .db
                    .query_row("SELECT COUNT(*) FROM items", [], |r| r.get(0))?;
                let versions: i64 =
                    self.db
                        .query_row("SELECT COUNT(*) FROM versions", [], |r| r.get(0))?;
                Ok(
                    json!({"deviceId":identity("deviceId")?,"dataStoreId":identity("dataStoreId")?,"itemCount":items,"versionCount":versions,"status":"healthy"}),
                )
            }
            _ => Err(Error::new(
                "UnsupportedOperation",
                "operation not supported",
            )),
        }
    }
}
fn run(root: &str, actor: &str, command: &str) -> Result<Value> {
    let actor = Actor::parse(actor)?;
    let command: Value = serde_json::from_str(command)
        .map_err(|_| Error::new("InvalidArgument", "invalid command JSON"))?;
    let op = text(&command, "op")?;
    let args = command
        .get("args")
        .filter(|v| v.is_object())
        .ok_or_else(|| Error::new("InvalidArgument", "args must be an object"))?;
    let mut repository = Repository::open(root)?;
    repository.db.execute_batch("BEGIN IMMEDIATE")?;
    let result = repository.dispatch(&actor, op, args)?;
    #[cfg(test)]
    recovery::crash_point("ready");
    repository.db.execute_batch("COMMIT")?;
    Ok(result)
}
pub fn execute(root: &str, actor: &str, command: &str) -> Value {
    match run(root, actor, command) {
        Ok(result) => json!({"ok":true,"result":result}),
        Err(e) => json!({"ok":false,"error":{"code":e.code,"message":e.message}}),
    }
}
