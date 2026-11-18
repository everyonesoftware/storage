package com.everyonesoftware.storage.service
import org.junit.Assert.*
import org.junit.Test
class CallerPolicyTest {
 @Test fun browserRequiresTrustedSignerAndPermission() {
  assertFalse(CallerPolicy.allowed(setOf("com.everyonesoftware.storage.browser"), false, true, false))
  assertFalse(CallerPolicy.allowed(setOf("com.everyonesoftware.storage.browser"), true, false, false))
  assertFalse(CallerPolicy.allowed(setOf("evil"), true, true, false))
  assertTrue(CallerPolicy.allowed(setOf("com.everyonesoftware.storage.browser"), true, true, false))
  assertTrue(CallerPolicy.allowed(emptySet(), false, false, true))
 }
}
