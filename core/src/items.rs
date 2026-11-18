use super::*;
impl Repository {
    pub(super) fn now(&self) -> Result<String> {
        Ok(self
            .db
            .query_row("SELECT strftime('%Y-%m-%dT%H:%M:%fZ','now')", [], |r| {
                r.get(0)
            })?)
    }
    pub(super) fn policy(&self, item: &str) -> Result<Value> {
        self.db.query_row("SELECT p.id,p.revision,p.owner,p.readers FROM policies p JOIN items i ON i.policy_id=p.id WHERE i.id=?1",[item],|r|{let readers:String=r.get(3)?;Ok(json!({"id":r.get::<_,String>(0)?,"revision":r.get::<_,i64>(1)?,"ownerPrincipalId":r.get::<_,String>(2)?,"readers":serde_json::from_str::<Value>(&readers).unwrap_or(Value::Null)}))}).optional()?.ok_or_else(||Error::new("NotFound","item not found"))
    }
    pub(super) fn authorize(&self, actor: &Actor, item: &str, write: bool) -> Result<Value> {
        let policy = self.policy(item)?;
        let owner = policy["ownerPrincipalId"] == actor.principal;
        if owner
            || (!write
                && policy["readers"]
                    .as_array()
                    .is_some_and(|r| r.iter().any(|x| x == &actor.principal)))
        {
            Ok(policy)
        } else {
            Err(Error::new("PermissionDenied", "item policy denies access"))
        }
    }
    pub(super) fn item(&self, item: &str) -> Result<Value> {
        self.db.query_row("SELECT i.id,i.name,i.content_type,i.revision,i.current_version,i.policy_id,p.revision,i.created,i.modified,v.bytes,v.sha FROM items i JOIN policies p ON i.policy_id=p.id JOIN versions v ON v.id=i.current_version WHERE i.id=?1",[item],|r|Ok(json!({"id":r.get::<_,String>(0)?,"name":r.get::<_,String>(1)?,"contentType":r.get::<_,String>(2)?,"revision":r.get::<_,i64>(3)?,"currentVersionId":r.get::<_,String>(4)?,"accessPolicyId":r.get::<_,String>(5)?,"accessPolicyRevision":r.get::<_,i64>(6)?,"createdAt":r.get::<_,String>(7)?,"modifiedAt":r.get::<_,String>(8)?,"attributes":{"byteLength":r.get::<_,i64>(9)?,"sha256":r.get::<_,String>(10)?},"properties":{}}))).optional()?.ok_or_else(||Error::new("NotFound","item not found"))
    }
    pub(super) fn version(&self, version: &str) -> Result<Value> {
        self.db.query_row("SELECT id,item_id,sha,bytes,created FROM versions WHERE id=?1",[version],|r|Ok(json!({"id":r.get::<_,String>(0)?,"itemId":r.get::<_,String>(1)?,"sha256":r.get::<_,String>(2)?,"byteLength":r.get::<_,i64>(3)?,"createdAt":r.get::<_,String>(4)?}))).optional()?.ok_or_else(||Error::new("NotFound","version not found"))
    }
    pub(super) fn create_item(&self, actor: &Actor, args: &Value) -> Result<Value> {
        actor.require("createItems")?;
        let name = text(args, "name")?;
        let content_type = text(args, "contentType")?;
        let source = Path::new(text(args, "sourcePath")?);
        let (sha, bytes) = blob::ingest(&self.root, source)?;
        let item = id();
        let version = id();
        let policy = id();
        let now = self.now()?;
        self.db.execute(
            "INSERT INTO policies VALUES (?1,?2,1,'[]')",
            params![policy, actor.principal],
        )?;
        self.db.execute(
            "INSERT INTO items VALUES (?1,?2,?3,1,?4,?5,?6,?6)",
            params![item, name, content_type, version, policy, now],
        )?;
        #[cfg(test)]
        super::recovery::crash_point("metadata");
        self.db.execute(
            "INSERT INTO versions VALUES (?1,?2,?3,?4,?5)",
            params![
                version,
                item,
                sha,
                i64::try_from(bytes)
                    .map_err(|_| Error::new("InvalidArgument", "file too large"))?,
                now
            ],
        )?;
        self.item(&item)
    }
    pub(super) fn list_items(&self, actor: &Actor) -> Result<Value> {
        actor.require("readItems")?;
        let mut statement=self.db.prepare("SELECT i.id FROM items i JOIN policies p ON i.policy_id=p.id WHERE p.owner=?1 OR EXISTS (SELECT 1 FROM json_each(p.readers) WHERE value=?1) ORDER BY i.created,i.id LIMIT 501")?;
        let ids = statement
            .query_map([&actor.principal], |r| r.get::<_, String>(0))?
            .collect::<std::result::Result<Vec<_>, _>>()?;
        bounded_list("items", ids.iter().map(|i| self.item(i)))
    }
}
