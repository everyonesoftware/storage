package com.everyonesoftware.storage.browser.updates

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.flow.MutableStateFlow
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.TimeoutException

class UpdateFinalizationTest {
 @Test fun failedCancellationRetainsOwnershipAndAllowsExplicitCleanupRetry() {
  for (failure in listOf(IllegalStateException("journal commit failed"), TimeoutException("sessions not released"))) {
   for (sessionVisible in listOf(true, false)) {
    val gate = UpdateGate()
    val state = MutableStateFlow(UpdateState(busy = true, ready = true, session = sessionVisible, confirmation = true))
    // The installer owns this evidence. A failed cleanup must not discard it.
    val journal = mutableMapOf("session" to "41", "sessions" to "41,42", "token" to "owned-attempt")
    val original = journal.toMap()
    var attempts = 0
    val cancel = {
     attempts++
     if (attempts == 1) throw failure
     journal.clear()
    }
    val escaped = runCatching {
     runBlocking {
      val active = CompletableDeferred<Unit>()
      val finish = CompletableDeferred<Unit>()
      val operation = launch {
       assertTrue(gate.enter())
       active.complete(Unit)
       try { finish.await() } finally { finishUpdateOperation(gate, state, cancel) }
      }
      active.await()
      assertFalse("An operation is still active when cancellation arrives", gate.enter())
      gate.cancel()
      finish.complete(Unit)
      operation.join()
     }
    }.exceptionOrNull()
    assertNull("Cleanup exceptions must be caught inside finalization", escaped)
    assertFalse(state.value.busy)
    assertFalse(state.value.ready)
    assertFalse(state.value.confirmation)
    assertEquals(sessionVisible, state.value.session)
    assertTrue(state.value.cleanupPending)
    assertTrue(state.value.message.contains("cleanup", ignoreCase = true))
    assertTrue(state.value.message.contains("Cancel update"))
    assertEquals(original, journal)
    assertTrue("Even failed cleanup releases the operation gate", gate.enter())
    gate.cancel()
    state.value = state.value.copy(busy = true)
    finishUpdateOperation(gate, state, cancel)
    assertEquals(2, attempts)
    assertTrue(journal.isEmpty())
    assertFalse(state.value.busy)
    assertFalse(state.value.session)
    assertFalse(state.value.cleanupPending)
    assertTrue("A successful retry leaves the controller usable", gate.enter())
    state.value = state.value.copy(busy = true)
    finishUpdateOperation(gate, state) { fail("Normal operations must not cancel installer sessions") }
    assertFalse(state.value.busy)
    assertTrue(gate.enter())
    gate.leave()
   }
  }
 }
}
