package com.everyonesoftware.storage.browser.updates

import org.junit.Assert.*
import org.junit.Test

class UpdatePolicyTest {
 private val service = ApkIdentity(SERVICE_PACKAGE, 1, "0.1.0", listOf("key"), false)
 private val browser = service.copy(packageName = BROWSER_PACKAGE)
 @Test fun everySupportedApiRequiresObservedServiceVersionBeforeBrowser() {
  for (api in listOf(26, 28, 29, 31, 36)) {
   assertEquals(listOf(SERVICE_PACKAGE), UpdatePolicy.plan(api, 4, service, browser))
   assertEquals(listOf(BROWSER_PACKAGE), UpdatePolicy.plan(api, 4, service.copy(code = 4), browser))
   assertEquals(listOf(SERVICE_PACKAGE), UpdatePolicy.plan(api, 4, service, browser.copy(code = 4)))
   assertEquals(emptyList<String>(), UpdatePolicy.plan(api, 4, service.copy(code = 4), browser.copy(code = 4)))
  }
 }
 @Test fun refusesInvalidInstalledIdentitiesBeforeOfferingEitherPackage() {
  for (bad in listOf(service.copy(signers = listOf("")), service.copy(signers = emptyList()), service.copy(packageName = "missing.product"))) {
   assertThrows(IllegalStateException::class.java) { UpdatePolicy.plan(29, 4, bad, browser.copy(signers = bad.signers)) }
  }
  assertThrows(IllegalStateException::class.java) { UpdatePolicy.plan(29, 4, service, browser.copy(signers = listOf("alien"))) }
  assertThrows(IllegalStateException::class.java) { UpdatePolicy.plan(29, 4, service, browser.copy(code = 5)) }
 }
 @Test fun rejectsDowngradeOfEitherInstalledPackage() {
  assertThrows(IllegalStateException::class.java) { UpdatePolicy.plan(29, 4, service.copy(code = 5), browser) }
 }
 @Test fun signingMismatchHasActionableInPlaceUpgradeError() {
  val error = assertThrows(SigningIdentityMismatch::class.java) {
   UpdatePolicy.verify(4, SERVICE_PACKAGE, service, service.copy(code = 4, version = "0.1.4", signers = listOf("other")))
  }
  assertTrue(error.message!!.contains("do not uninstall", true))
 }
 @Test fun rejectsWrongPackageVersionAndSigner() {
  val good = service.copy(code = 4, version = "0.1.4")
  UpdatePolicy.verify(4, SERVICE_PACKAGE, service, good)
  for (bad in listOf(good.copy(packageName = BROWSER_PACKAGE), good.copy(code = 3), good.copy(version = "0.1.3"), good.copy(signers = listOf("alien")), good.copy(signers = emptyList()), good.copy(debuggable = true))) {
   assertThrows(IllegalStateException::class.java) { UpdatePolicy.verify(4, SERVICE_PACKAGE, service, bad) }
  }
 }
 @Test fun cancellationIsObservedBeforeInstallHandoffAndResetOnlyByNextOperation() {
  val gate = UpdateGate(); assertTrue(gate.enter()); gate.cancel()
  assertThrows(java.util.concurrent.CancellationException::class.java) { gate.checkActive() }
  assertFalse(gate.enter()); gate.leave(); assertTrue(gate.enter()); gate.checkActive()
 }
 @Test fun concurrentActionsAreRefusedAndFailureReleasesGate() {
  val gate = UpdateGate()
  assertTrue(gate.enter()); assertFalse(gate.enter()); gate.leave(); assertTrue(gate.enter())
 }
}
