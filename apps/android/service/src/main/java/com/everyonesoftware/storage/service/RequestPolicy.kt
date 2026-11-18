package com.everyonesoftware.storage.service
object RequestPolicy {
 val fields = mapOf(
  "health" to emptySet(), "listItems" to emptySet(), "getItem" to setOf("itemId"),
  "listVersions" to setOf("itemId"), "renameItem" to setOf("itemId", "expectedRevision", "name"),
  "listCollections" to emptySet(), "createCollection" to setOf("name"),
  "listCollectionItems" to setOf("collectionId"),
  "addToCollection" to setOf("collectionId", "itemId", "expectedRevision"),
  "removeFromCollection" to setOf("collectionId", "itemId", "expectedRevision"),
  "deleteCollection" to setOf("collectionId", "expectedRevision"),
  "setAccessPolicy" to setOf("itemId", "expectedRevision", "readers")
 )
 fun allowed(operation: String, fields: Set<String>): Boolean = this.fields[operation] == fields
}
