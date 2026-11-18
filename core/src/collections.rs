use super::*;
impl Repository {
    fn collection(&self, actor: &Actor, collection: &str) -> Result<Value> {
        let (record,owner)=self.db.query_row("SELECT id,name,revision,created,owner FROM collections WHERE id=?1",[collection],|r|Ok((json!({"id":r.get::<_,String>(0)?,"name":r.get::<_,String>(1)?,"revision":r.get::<_,i64>(2)?,"createdAt":r.get::<_,String>(3)?}),r.get::<_,String>(4)?))).optional()?.ok_or_else(||Error::new("NotFound","collection not found"))?;
        if owner != actor.principal {
            return Err(Error::new("PermissionDenied", "collection is private"));
        }
        Ok(record)
    }
    pub(super) fn collections(&self, actor: &Actor, op: &str, args: &Value) -> Result<Value> {
        match op {
            "createCollection" => {
                actor.require("manageCollections")?;
                let collection = id();
                self.db.execute(
                    "INSERT INTO collections VALUES (?1,?2,?3,1,?4)",
                    params![
                        collection,
                        text(args, "name")?,
                        actor.principal,
                        self.now()?
                    ],
                )?;
                self.collection(actor, &collection)
            }
            "listCollections" => {
                actor.require("readItems")?;
                let mut s = self.db.prepare(
                    "SELECT id FROM collections WHERE owner=?1 ORDER BY created,id LIMIT 501",
                )?;
                let ids = s
                    .query_map([&actor.principal], |r| r.get::<_, String>(0))?
                    .collect::<std::result::Result<Vec<_>, _>>()?;
                bounded_list("collections", ids.iter().map(|i| self.collection(actor, i)))
            }
            "listCollectionItems" => {
                actor.require("readItems")?;
                let collection = text(args, "collectionId")?;
                self.collection(actor, collection)?;
                let mut s=self.db.prepare("SELECT m.item_id FROM memberships m JOIN items i ON m.item_id=i.id JOIN policies p ON i.policy_id=p.id WHERE m.collection_id=?1 AND (p.owner=?2 OR EXISTS (SELECT 1 FROM json_each(p.readers) WHERE value=?2)) ORDER BY m.item_id LIMIT 501")?;
                let ids = s
                    .query_map(params![collection, actor.principal], |r| {
                        r.get::<_, String>(0)
                    })?
                    .collect::<std::result::Result<Vec<_>, _>>()?;
                bounded_list("items", ids.iter().map(|i| self.item(i)))
            }
            "addToCollection" | "removeFromCollection" | "deleteCollection" => {
                actor.require("manageCollections")?;
                let collection = text(args, "collectionId")?;
                let record = self.collection(actor, collection)?;
                mutations::revision(args, &record["revision"])?;
                if op == "deleteCollection" {
                    self.db
                        .execute("DELETE FROM collections WHERE id=?1", [collection])?;
                    return Ok(json!({"deleted":true}));
                }
                let item = text(args, "itemId")?;
                if op == "addToCollection" {
                    actor.require("readItems")?;
                    self.authorize(actor, item, false)?;
                    self.db.execute(
                        "INSERT OR IGNORE INTO memberships VALUES (?1,?2)",
                        params![collection, item],
                    )?;
                } else {
                    self.db.execute(
                        "DELETE FROM memberships WHERE collection_id=?1 AND item_id=?2",
                        params![collection, item],
                    )?;
                }
                self.db.execute(
                    "UPDATE collections SET revision=revision+1 WHERE id=?1",
                    [collection],
                )?;
                self.collection(actor, collection)
            }
            _ => Err(Error::new(
                "UnsupportedOperation",
                "operation not supported",
            )),
        }
    }
}
