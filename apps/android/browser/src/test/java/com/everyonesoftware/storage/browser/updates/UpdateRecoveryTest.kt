package com.everyonesoftware.storage.browser.updates

import org.junit.Assert.*
import org.junit.Test

class UpdateRecoveryTest {
 @Test fun uncommittedPreparationAlwaysBecomesExplicitRetry() {
  for (phase in listOf(null, "preparing", "prepared", "committing")) {
   assertFalse("$phase must not wait forever", UpdateRecovery.keep(phase, true, false, false))
  }
 }
 @Test fun sealedSessionsSurviveDeathEvenBeforeCommittedPhaseWasSaved() {
  for (phase in listOf(null, "preparing", "prepared", "committing", "committed")) {
   assertTrue(UpdateRecovery.keep(phase, true, true, false))
  }
 }
 @Test fun durableCommittedPhaseOrPendingUserActionIsNotAbandoned() {
  assertTrue(UpdateRecovery.keep("committed", true, false, false))
  assertTrue(UpdateRecovery.keep("committing", true, false, true))
 }
 @Test fun vanishedSessionsNeverLeaveTheUserWaiting() {
  assertFalse(UpdateRecovery.keep("committed", false, true, true))
 }
}
