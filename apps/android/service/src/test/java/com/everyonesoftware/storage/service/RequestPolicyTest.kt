package com.everyonesoftware.storage.service
import org.junit.Assert.*
import org.junit.Test
class RequestPolicyTest {
 @Test fun onlyLogicalQueriesAndExactFieldsAreAllowed() {
  assertTrue(RequestPolicy.allowed("health", emptySet()))
  assertTrue(RequestPolicy.allowed("getItem", setOf("itemId")))
  assertFalse(RequestPolicy.allowed("readVersion", setOf("versionId")))
  assertFalse(RequestPolicy.allowed("createItem", setOf("sourcePath")))
  assertFalse(RequestPolicy.allowed("health", setOf("repository")))
  assertFalse(RequestPolicy.allowed("getItem", emptySet()))
  assertFalse(RequestPolicy.allowed("getItem", setOf("itemId", "actor")))
 }
}
