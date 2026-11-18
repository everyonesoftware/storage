package com.everyonesoftware.storage.service

import android.content.Context
import android.os.ParcelFileDescriptor
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.client.StorageConnection
import com.everyonesoftware.storage.client.IStorage
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class StorageBinderTest {
 private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
 private fun result(value: String): JSONObject {
  val response = JSONObject(value)
  assertTrue(value, response.getBoolean("ok"))
  return response.getJSONObject("result")
 }
 private fun import(api: IStorage, file: File) = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { result(api.importFile(file.name, "application/octet-stream", it)) }
 @Test fun importVersionReadAndRebindKeepExactBytes() {
  val file = File(context.cacheDir, "binder-${System.nanoTime()}.bin")
  val original = ByteArray(131_079) { (it % 251).toByte() }; file.writeBytes(original)
  val first = StorageConnection(context)
  val api = first.connect().get(20, TimeUnit.SECONDS)
  val before = result(api.query("health", "{}"))
  val item = import(api, file)
  val oldId = item.getString("currentVersionId")
  assertArrayEquals(original, ParcelFileDescriptor.AutoCloseInputStream(api.readVersion(oldId)).use { it.readBytes() })
  val next = "new immutable version\u0000".toByteArray(); file.writeBytes(next)
  val updated = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { result(api.addVersion(item.getString("id"), item.getLong("revision"), it)) }
  assertNotEquals(oldId, updated.getString("currentVersionId"))
  assertArrayEquals(original, ParcelFileDescriptor.AutoCloseInputStream(api.readVersion(oldId)).use { it.readBytes() })
  assertArrayEquals(next, ParcelFileDescriptor.AutoCloseInputStream(api.readVersion(updated.getString("currentVersionId"))).use { it.readBytes() })
  assertEquals(2, result(api.query("listVersions", JSONObject().put("itemId", item.getString("id")).toString())).getJSONArray("versions").length())
  val listed = result(api.query("listItems", "{}")).getJSONArray("items")
  assertTrue((0 until listed.length()).any { listed.getJSONObject(it).getString("id") == item.getString("id") })
  first.close()
  // A fresh binding reopens the durable root; parent also runs cold process restart probes.
  StorageConnection(context).use { second ->
   val reopened = second.connect().get(20, TimeUnit.SECONDS)
   assertEquals(before.getString("dataStoreId"), result(reopened.query("health", "{}")).getString("dataStoreId"))
   assertArrayEquals(original, ParcelFileDescriptor.AutoCloseInputStream(reopened.readVersion(oldId)).use { it.readBytes() })
  }
  file.delete()
 }
 @Test fun idleImportsCannotStarveMetadataAndExpireWithoutPublishing() {
  StorageConnection(context).use { connection ->
   val api = connection.connect().get(20, TimeUnit.SECONDS)
   val before = result(api.query("health", "{}")).getLong("itemCount")
   val workers = java.util.concurrent.Executors.newFixedThreadPool(3)
   val pipes = List(2) { ParcelFileDescriptor.createPipe() }
   try {
    val imports = pipes.mapIndexed { index, pipe -> workers.submit<String> {
     pipe[0].use { api.importFile("idle-$index", "application/octet-stream", it) }
    } }
    Thread.sleep(500)
    val metadata = workers.submit<String> { api.query("health", "{}") }
    assertEquals("Metadata must remain responsive with two idle imports", before, result(metadata.get(3, TimeUnit.SECONDS)).getLong("itemCount"))
    ParcelFileDescriptor.createPipe().let { overflow ->
     try {
      val rejected = workers.submit<String> { overflow[0].use { api.importFile("overflow", "application/octet-stream", it) } }
      assertFalse("Import admission must be bounded, not queued", JSONObject(rejected.get(3, TimeUnit.SECONDS)).getBoolean("ok"))
     } finally { overflow.forEach { runCatching { it.close() } } }
    }
    imports.forEach { future ->
     val response = JSONObject(future.get(15, TimeUnit.SECONDS))
     assertFalse("Idle import must time out", response.getBoolean("ok"))
    }
    pipes.forEach { pipe ->
     try {
      android.system.Os.write(pipe[1].fileDescriptor, byteArrayOf(1), 0, 1)
      fail("Service must close its owned read descriptor on timeout")
     } catch (e: android.system.ErrnoException) { assertEquals(android.system.OsConstants.EPIPE, e.errno) }
    }
    assertEquals(before, result(api.query("health", "{}")).getLong("itemCount"))
   } finally {
    pipes.flatMap { it.toList() }.forEach { runCatching { it.close() } }
    workers.shutdownNow()
   }
  }
 }
 @Test fun bindingDeathAfterConnectionRebindsSameClient() {
  var callback: android.content.ServiceConnection? = null
  var unbinds = 0
  val wrapped = object : android.content.ContextWrapper(context) {
   override fun bindService(intent: android.content.Intent, connection: android.content.ServiceConnection, flags: Int): Boolean {
    callback = connection
    return super.bindService(intent, connection, flags)
   }
   override fun unbindService(connection: android.content.ServiceConnection) { unbinds++; super.unbindService(connection) }
  }
  StorageConnection(wrapped).use { client ->
   val first = client.connect()
   val before = result(first.get(20, TimeUnit.SECONDS).query("health", "{}"))
   InstrumentationRegistry.getInstrumentation().runOnMainSync {
    callback!!.onBindingDied(android.content.ComponentName(context, StorageService::class.java))
   }
   val next = client.connect()
   assertNotSame("A dead binding must not retain its completed future", first, next)
   assertEquals(1, unbinds)
   assertEquals(before.getString("dataStoreId"), result(next.get(20, TimeUnit.SECONDS).query("health", "{}")).getString("dataStoreId"))
  }
 }
 @Test fun cancelledReliableImportNeverPublishesPartialBytes() {
  StorageConnection(context).use { client ->
   val api = client.connect().get(20, TimeUnit.SECONDS)
   val before = result(api.query("health", "{}")).getLong("itemCount")
   val pipe = ParcelFileDescriptor.createReliablePipe()
   val workers = java.util.concurrent.Executors.newSingleThreadExecutor()
   try {
    val imported = workers.submit<String> { pipe[0].use { api.importFile("cancelled", "application/octet-stream", it) } }
    android.system.Os.write(pipe[1].fileDescriptor, byteArrayOf(1, 2, 3), 0, 3)
    pipe[1].closeWithError("Producer cancelled")
    assertFalse(JSONObject(imported.get(5, TimeUnit.SECONDS)).getBoolean("ok"))
    assertEquals(before, result(api.query("health", "{}")).getLong("itemCount"))
   } finally { pipe.forEach { runCatching { it.close() } }; workers.shutdownNow() }
  }
 }
 @Test fun bindingDeathWhilePendingFailsOldWaiterAndIgnoresStaleCallback() {
  var callback: android.content.ServiceConnection? = null
  val wrapped = object : android.content.ContextWrapper(context) {
   override fun bindService(intent: android.content.Intent, connection: android.content.ServiceConnection, flags: Int): Boolean {
    callback = connection
    return super.bindService(intent, connection, flags)
   }
  }
  StorageConnection(wrapped).use { client ->
   lateinit var first: java.util.concurrent.CompletableFuture<IStorage>
   lateinit var next: java.util.concurrent.CompletableFuture<IStorage>
   InstrumentationRegistry.getInstrumentation().runOnMainSync {
    first = client.connect()
    val stale = callback!!
    stale.onBindingDied(android.content.ComponentName(context, StorageService::class.java))
    next = client.connect()
    stale.onNullBinding(android.content.ComponentName(context, StorageService::class.java))
   }
   assertTrue(first.isCompletedExceptionally)
   result(next.get(20, TimeUnit.SECONDS).query("health", "{}"))
   client.close()
   val reopened = client.connect()
   assertNotSame(next, reopened)
   result(reopened.get(20, TimeUnit.SECONDS).query("health", "{}"))
  }
 }
 @Test fun serviceDestructionCancelsOwnedIdleDescriptorWithoutPublishing() {
  val client = StorageConnection(context)
  val api = client.connect().get(20, TimeUnit.SECONDS)
  val before = result(api.query("health", "{}")).getLong("itemCount")
  val pipe = ParcelFileDescriptor.createPipe()
  val worker = java.util.concurrent.Executors.newSingleThreadExecutor()
  try {
   val imported = worker.submit<String> { pipe[0].use { api.importFile("destroyed", "application/octet-stream", it) } }
   android.system.Os.write(pipe[1].fileDescriptor, byteArrayOf(1), 0, 1)
   Thread.sleep(500)
   InstrumentationRegistry.getInstrumentation().runOnMainSync { client.close() }
   assertFalse(JSONObject(imported.get(5, TimeUnit.SECONDS)).getBoolean("ok"))
   val reopened = client.connect().get(20, TimeUnit.SECONDS)
   assertEquals(before, result(reopened.query("health", "{}")).getLong("itemCount"))
  } finally { client.close(); pipe.forEach { runCatching { it.close() } }; worker.shutdownNow() }
 }
 @Test fun unavailableReadReturnsSafeSecurityExceptionOverBinder() {
  StorageConnection(context).use { client ->
   val api = client.connect().get(20, TimeUnit.SECONDS)
   try { api.readVersion("missing-version").close(); fail("Missing content must be denied") }
   catch (e: SecurityException) { assertEquals("Version unavailable or not authorized", e.message) }
  }
 }
 @Test fun arbitraryNativeCommandsAndPathsAreRejected() {
  StorageConnection(context).use { connection ->
   val api = connection.connect().get(20, TimeUnit.SECONDS)
   for ((op, args) in listOf("readVersion" to "{}", "createItem" to "{}", "health" to "{\"sourcePath\":\"/etc/passwd\"}", "health" to "{\"actor\":{}}")) {
    assertFalse(JSONObject(api.query(op, args)).getBoolean("ok"))
   }
  }
 }
}
