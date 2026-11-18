package com.everyonesoftware.storage.browser

import android.os.ParcelFileDescriptor
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.client.StorageConnection
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class ColdBindingRestartTest {
 @Test fun sameClientReconnectsAfterActualServiceProcessDeath() {
  val instrumentation = InstrumentationRegistry.getInstrumentation()
  val context = instrumentation.targetContext
  val file = File(context.cacheDir, "cold-binding.bin").apply { writeBytes(byteArrayOf(5, 0, 7, 9)) }
  fun result(raw: String): JSONObject = JSONObject(raw).let { assertTrue(raw, it.getBoolean("ok")); it.getJSONObject("result") }
  try {
   StorageConnection(context).use { client ->
    val first = client.connect()
    val api = first.get(20, TimeUnit.SECONDS)
    val before = result(api.query("health", "{}"))
    val item = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { result(api.importFile("Cold binding fixture", "application/octet-stream", it)) }
    ParcelFileDescriptor.AutoCloseInputStream(instrumentation.uiAutomation.executeShellCommand("am force-stop com.everyonesoftware.storage.service")).use { it.readBytes() }
    val deadline = SystemClock.elapsedRealtime() + 10_000
    while (api.asBinder().pingBinder() && SystemClock.elapsedRealtime() < deadline) Thread.sleep(50)
    assertFalse("The test must actually kill the old native service process", api.asBinder().pingBinder())
    instrumentation.runOnMainSync { }
    val next = client.connect()
    assertNotSame(first, next)
    val reopened = next.get(20, TimeUnit.SECONDS)
    assertEquals(before.getString("dataStoreId"), result(reopened.query("health", "{}")).getString("dataStoreId"))
    assertArrayEquals(file.readBytes(), ParcelFileDescriptor.AutoCloseInputStream(reopened.readVersion(item.getString("currentVersionId"))).use { it.readBytes() })
   }
  } finally { file.delete() }
 }
}
