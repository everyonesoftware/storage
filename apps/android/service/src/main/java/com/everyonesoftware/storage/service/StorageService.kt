package com.everyonesoftware.storage.service

import android.app.Service
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Binder
import android.os.ParcelFileDescriptor
import android.os.Process
import com.everyonesoftware.storage.client.IStorage
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Callable
import java.util.concurrent.ExecutionException
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.Semaphore
import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import android.system.StructPollfd
import java.io.IOException

/** Authentication and command reconstruction happen here, never in a client-controlled actor. */
class StorageService : Service() {
 private val io = ThreadPoolExecutor(2, 2, 0, TimeUnit.MILLISECONDS, ArrayBlockingQueue(16))
 private val importSlots = Semaphore(2)
 private val activeSources = mutableSetOf<ParcelFileDescriptor>()
 @Volatile private var stopping = false
 private val root by lazy { File(filesDir, "repository").apply { mkdirs() }.absolutePath }
 private val actor = JSONObject().put("principalId", "local-owner").put("capabilities", JSONArray(listOf("readItems", "createItems", "createVersions", "modifyItems", "manageAccess", "manageCollections", "readDiagnostics"))).toString()
 private fun authenticate() {
  val uid = Binder.getCallingUid()
  val packages = packageManager.getPackagesForUid(uid)?.toSet().orEmpty()
  val signed = packageManager.checkSignatures(Process.myUid(), uid) == PackageManager.SIGNATURE_MATCH
  val permitted = checkCallingOrSelfPermission(PERMISSION) == PackageManager.PERMISSION_GRANTED
  if (!CallerPolicy.allowed(packages, signed, permitted, uid == Process.myUid())) throw SecurityException("Storage caller is not granted access")
 }
 private fun execute(op: String, args: JSONObject): String {
  val response = NativeCore.execute(root, actor, JSONObject().put("op", op).put("args", args).toString())
  require(response.toByteArray().size <= 512 * 1024) { "Response exceeds local IPC bound" }
  val envelope = JSONObject(response)
  if (!envelope.getBoolean("ok")) {
   val code = envelope.getJSONObject("error").optString("code", "InternalError")
   return error(code, when (code) {
    "RevisionConflict" -> "The record changed. Refresh before retrying."
    "NotFound" -> "The requested record was not found."
    "PermissionDenied" -> "The current principal does not have access."
    "IntegrityError" -> "Content integrity verification failed."
    "InvalidArgument" -> "Invalid Storage request."
    else -> "Storage operation failed ($code)."
   })
  }
  return response
 }
 private fun error(code: String, message: String) = JSONObject().put("ok", false).put("error", JSONObject().put("code", code).put("message", message)).toString()
 private fun <T> awaitOperation(block: () -> T): T {
  val future = io.submit(Callable { block() })
  try { return future.get(30, TimeUnit.SECONDS) }
  catch (e: ExecutionException) { throw (e.cause ?: e) }
  catch (e: Exception) { future.cancel(true); throw e }
 }
 private fun operationResult(block: () -> String): String = try { block() } catch (e: Exception) {
  if (e is InterruptedException) Thread.currentThread().interrupt()
  error(if (e is IllegalArgumentException || e is org.json.JSONException) "InvalidArgument" else "IoError", if (e is IllegalArgumentException) e.message ?: "Invalid request" else "Storage operation failed")
 }
 private fun runStorageOperation(block: () -> String): String = operationResult { awaitOperation(block) }
 private fun importSource(source: ParcelFileDescriptor, block: () -> String): String = source.use {
  authenticate()
  operationResult {
   check(importSlots.tryAcquire()) { "Import capacity exhausted" }
   try {
    synchronized(activeSources) { check(!stopping); activeSources.add(source) }
    block()
   } finally {
    synchronized(activeSources) { activeSources.remove(source) }
    importSlots.release()
   }
  }
 }
 private fun stage(source: ParcelFileDescriptor, op: String, args: JSONObject): String {
  val file = File.createTempFile("import-", ".stage", cacheDir)
  val started = android.os.SystemClock.elapsedRealtime()
  var lastProgress = started
  fun checkDeadline() {
   val now = android.os.SystemClock.elapsedRealtime()
   if (stopping || Thread.currentThread().isInterrupted || now - started >= 60_000 || now - lastProgress >= 10_000) throw IOException("Import cancelled or timed out")
  }
  try {
   if (DescriptorIo.makeNonblocking(source.fd) != 0) throw IOException("Cannot configure import descriptor")
   file.outputStream().use { output ->
    val buffer = ByteArray(64 * 1024); var total = 0L
    val poll = StructPollfd().apply { fd = source.fileDescriptor; events = OsConstants.POLLIN.toShort() }
    while (true) {
     checkDeadline()
     if (Os.poll(arrayOf(poll), 250) == 0) continue
     checkDeadline()
     if (poll.revents.toInt() and (OsConstants.POLLERR or OsConstants.POLLNVAL) != 0) throw IOException("Import source failed")
     val count = try { Os.read(source.fileDescriptor, buffer, 0, buffer.size) }
     catch (e: android.system.ErrnoException) { if (e.errno == OsConstants.EAGAIN) continue else throw e }
     if (count == 0) break
     lastProgress = android.os.SystemClock.elapsedRealtime()
     total += count; require(total <= 1024L * 1024 * 1024) { "Local import limit is 1 GiB" }
     output.write(buffer, 0, count)
    }
    source.checkError()
    checkDeadline()
    output.fd.sync()
   }
   checkDeadline()
   return runStorageOperation { checkDeadline(); execute(op, args.put("sourcePath", file.absolutePath)) }
  } finally { file.delete() }
 }
 private val binder = object : IStorage.Stub() {
  override fun query(operation: String, arguments: String): String {
   authenticate()
   return runStorageOperation {
    require(arguments.length <= 16_384) { "Arguments too large" }
    val input = JSONObject(arguments)
    require(RequestPolicy.allowed(operation, input.keys().asSequence().toSet())) { "Unsupported operation or fields" }
    val args = JSONObject()
    for (key in RequestPolicy.fields.getValue(operation)) {
     when (key) {
      "expectedRevision" -> { val value = input.get(key); require((value is Int || value is Long) && (value as Number).toLong() >= 1) { "Invalid revision" }; args.put(key, value.toLong()) }
      "readers" -> { val readers = input.getJSONArray(key); require(readers.length() <= 100); val clean = JSONArray(); for (i in 0 until readers.length()) { val value = readers.get(i); require(value is String && value.length in 1..256); clean.put(value) }; args.put(key, clean) }
      else -> { val value = input.get(key); require(value is String && value.length in 1..512) { "Invalid field" }; args.put(key, value) }
     }
    }
    execute(operation, args)
   }
  }
  override fun importFile(name: String, contentType: String, source: ParcelFileDescriptor): String = importSource(source) {
   require(name.length in 1..512 && contentType.length in 1..256)
   stage(source, "createItem", JSONObject().put("name", name).put("contentType", contentType))
  }
  override fun addVersion(itemId: String, expectedRevision: Long, source: ParcelFileDescriptor): String = importSource(source) {
   require(itemId.length in 1..256 && expectedRevision >= 1)
   stage(source, "createVersion", JSONObject().put("itemId", itemId).put("expectedRevision", expectedRevision))
  }
  override fun readVersion(versionId: String): ParcelFileDescriptor {
   authenticate()
   require(versionId.length in 1..256)
   // Hash verification and access checks run inside the core before any handle is returned.
   return awaitOwnedOperation(io, 30, TimeUnit.SECONDS) {
    val envelope = JSONObject(execute("readVersion", JSONObject().put("versionId", versionId)))
    if (!envelope.getBoolean("ok")) throw SecurityException("Version unavailable or not authorized")
    val path = envelope.getJSONObject("result").getString("path")
    ParcelFileDescriptor.open(File(path), ParcelFileDescriptor.MODE_READ_ONLY)
   }
  }
 }
 override fun onBind(intent: Intent) = binder
 override fun onDestroy() {
  synchronized(activeSources) {
   stopping = true
   activeSources.forEach { runCatching { it.close() } }
   activeSources.clear()
  }
  io.shutdownNow()
  super.onDestroy()
 }
 companion object { const val PERMISSION = "com.everyonesoftware.storage.BIND_STORAGE" }
}
