package com.everyonesoftware.storage.service

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.*
import java.util.concurrent.atomic.AtomicInteger

class OwnedOperationTest {
 private class Resource : AutoCloseable {
  val closes = AtomicInteger()
  val closed = CountDownLatch(1)
  override fun close() { closes.incrementAndGet(); closed.countDown() }
 }
 @Test fun successfulHandoffLeavesResourceOwnedByCaller() {
  val executor = Executors.newSingleThreadExecutor()
  val resource = Resource()
  try {
   assertSame(resource, awaitOwnedOperation(executor, 2, TimeUnit.SECONDS) { resource })
   assertEquals(0, resource.closes.get())
   resource.close()
   assertEquals(1, resource.closes.get())
  } finally { executor.shutdownNow() }
 }
 @Test fun resourceCreatedAfterTimeoutIsClosedEvenWhenProducerIgnoresInterrupt() {
  val executor = Executors.newSingleThreadExecutor()
  val release = CountDownLatch(1)
  val resource = Resource()
  try {
   try {
    awaitOwnedOperation(executor, 50, TimeUnit.MILLISECONDS) {
     while (true) { try { release.await(); break } catch (_: InterruptedException) { /* JNI can ignore cancellation. */ } }
     resource
    }
    fail("Operation did not time out")
   } catch (_: TimeoutException) { }
   release.countDown()
   assertTrue("Abandoned late resource was not closed", resource.closed.await(2, TimeUnit.SECONDS))
   assertEquals(1, resource.closes.get())
  } finally { release.countDown(); executor.shutdownNow() }
 }
 @Test fun completedResourceIsClosedWhenTimeoutRacesWithHandoff() {
  // Deterministically model Future.get timing out just as the worker completes.
  val executor = object : AbstractExecutorService() {
   override fun execute(command: Runnable) = command.run()
   override fun <T> newTaskFor(callable: Callable<T>): RunnableFuture<T> = object : FutureTask<T>(callable) {
    override fun get(timeout: Long, unit: TimeUnit): T { super.get(timeout, unit); throw TimeoutException("Handoff race") }
   }
   override fun shutdown() = Unit
   override fun shutdownNow(): MutableList<Runnable> = mutableListOf()
   override fun isShutdown() = false
   override fun isTerminated() = false
   override fun awaitTermination(timeout: Long, unit: TimeUnit) = true
  }
  val resource = Resource()
  try { awaitOwnedOperation(executor, 1, TimeUnit.SECONDS) { resource }; fail("Expected timeout") }
  catch (_: TimeoutException) { }
  assertEquals("Completed but unclaimed resource leaked", 1, resource.closes.get())
 }
}
