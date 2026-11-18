package com.everyonesoftware.storage.service

import java.util.concurrent.Callable
import java.util.concurrent.ExecutionException
import java.util.concurrent.ExecutorService
import java.util.concurrent.TimeUnit

/** Transfers a produced resource exactly once, or closes it after cancellation.
 * Future cancellation alone cannot own the result of a JNI call that ignores
 * interruption: FutureTask discards that result. Keep ownership outside it.
 */
internal fun <T : AutoCloseable> awaitOwnedOperation(executor: ExecutorService, timeout: Long, unit: TimeUnit, producer: () -> T): T {
 val lock = Any()
 var abandoned = false
 var pending: T? = null
 val future = executor.submit(Callable {
  val produced = producer()
  val discard = synchronized(lock) {
   if (abandoned) true else { pending = produced; false }
  }
  if (discard) produced.close()
  Unit
 })
 try {
  future.get(timeout, unit)
  return synchronized(lock) {
   val claimed = checkNotNull(pending) { "Resource operation completed without a result" }
   pending = null
   claimed
  }
 } catch (e: Exception) {
  val unclaimed = synchronized(lock) {
   abandoned = true
   val value = pending
   pending = null
   value
  }
  future.cancel(true)
  runCatching { unclaimed?.close() }
  if (e is ExecutionException) throw (e.cause ?: e)
  throw e
 }
}
