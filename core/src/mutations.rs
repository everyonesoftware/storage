use super::*;
pub(super) fn revision(args: &Value, actual: &Value) -> Result<()> {
    let expected = args["expectedRevision"]
        .as_i64()
        .filter(|n| *n > 0)
        .ok_or_else(|| {
            Error::new(
                "InvalidArgument",
                "expectedRevision must be a positive integer",
            )
        })?;
    if actual.as_i64() == Some(expected) {
        Ok(())
    } else {
        Err(Error::new("RevisionConflict", "revision is stale"))
    }
}
impl Repository {
    pub(super) fn set_access(&self, actor: &Actor, args: &Value) -> Result<Value> {
        actor.require("manageAccess")?;
        let item = text(args, "itemId")?;
        let mut policy = self.authorize(actor, item, true)?;
        revision(args, &policy["revision"])?;
        let values = args["readers"]
            .as_array()
            .ok_or_else(|| Error::new("InvalidArgument", "readers must be an array"))?;
        let mut readers = std::collections::BTreeSet::new();
        for value in values {
            readers.insert(
                value
                    .as_str()
                    .filter(|s| !s.is_empty())
                    .ok_or_else(|| {
                        Error::new("InvalidArgument", "reader must be a nonempty principal")
                    })?
                    .to_owned(),
            );
        }
        let readers = json!(readers);
        self.db.execute(
            "UPDATE policies SET revision=revision+1,readers=?1 WHERE id=?2",
            params![readers.to_string(), text(&policy, "id")?],
        )?;
        let next = policy["revision"]
            .as_i64()
            .ok_or_else(|| Error::new("IntegrityError", "invalid policy revision"))?
            + 1;
        self.db.execute(
            "INSERT INTO audit VALUES (?1,?2,?3,?4,?5,?6)",
            params![
                id(),
                text(&policy, "id")?,
                actor.principal,
                next,
                readers.to_string(),
                self.now()?
            ],
        )?;
        policy["revision"] = json!(next);
        policy["readers"] = readers;
        Ok(policy)
    }
    pub(super) fn create_version(&self, actor: &Actor, args: &Value) -> Result<Value> {
        actor.require("createVersions")?;
        let item = text(args, "itemId")?;
        self.authorize(actor, item, true)?;
        let current = self.item(item)?;
        revision(args, &current["revision"])?;
        let (sha, bytes) = blob::ingest(&self.root, Path::new(text(args, "sourcePath")?))?;
        let version = id();
        let now = self.now()?;
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
        self.db.execute(
            "UPDATE items SET current_version=?1,revision=revision+1,modified=?2 WHERE id=?3",
            params![version, now, item],
        )?;
        self.item(item)
    }
    pub(super) fn list_versions(&self, actor: &Actor, args: &Value) -> Result<Value> {
        actor.require("readItems")?;
        let item = text(args, "itemId")?;
        self.authorize(actor, item, false)?;
        let mut s = self
            .db
            .prepare("SELECT id FROM versions WHERE item_id=?1 ORDER BY created,id LIMIT 501")?;
        let ids = s
            .query_map([item], |r| r.get::<_, String>(0))?
            .collect::<std::result::Result<Vec<_>, _>>()?;
        bounded_list("versions", ids.iter().map(|v| self.version(v)))
    }
    pub(super) fn rename(&self, actor: &Actor, args: &Value) -> Result<Value> {
        actor.require("modifyItems")?;
        let item = text(args, "itemId")?;
        self.authorize(actor, item, true)?;
        revision(args, &self.item(item)?["revision"])?;
        let name = text(args, "name")?;
        self.db.execute(
            "UPDATE items SET name=?1,revision=revision+1,modified=?2 WHERE id=?3",
            params![name, self.now()?, item],
        )?;
        self.item(item)
    }
}
