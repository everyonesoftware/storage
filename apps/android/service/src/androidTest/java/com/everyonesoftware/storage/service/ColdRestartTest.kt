package com.everyonesoftware.storage.service

import android.os.ParcelFileDescriptor
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.client.StorageConnection
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.concurrent.TimeUnit

/** Run explicitly with -e phase seed, force-stop the service, then -e phase verify. */
class ColdRestartTest {
 @Test fun durableBytesAndIdentityAfterProcessDeath() {
  val instrumentation = InstrumentationRegistry.getInstrumentation()
  val context = instrumentation.targetContext
  val phase = InstrumentationRegistry.getArguments().getString("phase")
  require(phase == "seed" || phase == "verify") { "Run this test explicitly with phase=seed or phase=verify" }
  val prefs = context.getSharedPreferences("cold-restart-fixture", 0)
  val bytes = ByteArray(65_539) { (it % 239).toByte() }
  StorageConnection(context).use { connection ->
   val api = connection.connect().get(30, TimeUnit.SECONDS)
   fun result(raw: String): JSONObject { val envelope = JSONObject(raw); assertTrue(raw, envelope.getBoolean("ok")); return envelope.getJSONObject("result") }
   val health = result(api.query("health", "{}"))
   if (phase == "seed") {
    val file = File(context.cacheDir, "cold-restart.bin").apply { writeBytes(bytes) }
    val item = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { result(api.importFile(file.name, "application/octet-stream", it)) }
    assertTrue(prefs.edit().putString("storeId", health.getString("dataStoreId")).putString("deviceId", health.getString("deviceId")).putString("versionId", item.getString("currentVersionId")).commit())
    file.delete()
   } else {
    assertEquals(prefs.getString("storeId", null), health.getString("dataStoreId"))
    assertEquals(prefs.getString("deviceId", null), health.getString("deviceId"))
    val version = requireNotNull(prefs.getString("versionId", null)) { "Seed phase must run before verify" }
    assertArrayEquals(bytes, ParcelFileDescriptor.AutoCloseInputStream(api.readVersion(version)).use { it.readBytes() })
   }
  }
 }
}
