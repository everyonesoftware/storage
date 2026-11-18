package com.everyonesoftware.storage.browser.updates

import android.os.ParcelFileDescriptor
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.client.StorageConnection
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.concurrent.TimeUnit

/** Explicit test-only fixture. Run seed before OS upgrades and verify after each installation.
 * Reads through the real Browser UID/Binder grant, never reads Service repository files.
 */
class UpdateContinuityTest {
 @Test fun browserGrantIdentityAndExactBytesSurviveUpgrade() {
  val context = InstrumentationRegistry.getInstrumentation().targetContext
  val phase = InstrumentationRegistry.getArguments().getString("phase")
  require(phase == "seed" || phase == "verify")
  val prefs = context.getSharedPreferences("update-continuity-fixture", 0)
  val bytes = ByteArray(65_539) { (it % 239).toByte() }
  StorageConnection(context).use { connection ->
   val api = connection.connect().get(30, TimeUnit.SECONDS)
   fun result(raw: String): JSONObject {
    val envelope = JSONObject(raw); assertTrue(raw, envelope.getBoolean("ok")); return envelope.getJSONObject("result")
   }
   val health = result(api.query("health", "{}"))
   if (phase == "seed") {
    val file = File(context.cacheDir, "update-continuity.bin").apply { writeBytes(bytes) }
    try {
     val item = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use {
      result(api.importFile("Update continuity test fixture", "application/octet-stream", it))
     }
     assertTrue(prefs.edit().putString("storeId", health.getString("dataStoreId"))
      .putString("deviceId", health.getString("deviceId"))
      .putString("versionId", item.getString("currentVersionId")).commit())
    } finally { file.delete() }
   }
   assertEquals(prefs.getString("storeId", null), health.getString("dataStoreId"))
   assertEquals(prefs.getString("deviceId", null), health.getString("deviceId"))
   val version = requireNotNull(prefs.getString("versionId", null)) { "Seed before upgrade" }
   assertArrayEquals(bytes, ParcelFileDescriptor.AutoCloseInputStream(api.readVersion(version)).use { it.readBytes() })
   android.util.Log.i("StorageUpdateContinuity", "phase=$phase store=${health.getString("dataStoreId")} device=${health.getString("deviceId")} version=$version exactBytes=${bytes.size}")
  }
 }
}
