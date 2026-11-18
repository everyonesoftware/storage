package com.everyonesoftware.storage.denied
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.client.StorageConnection
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.TimeUnit
/** Same debug signer and signature permission are insufficient without an explicit package grant. */
@RunWith(AndroidJUnit4::class)
class DeniedCallerTest {
 @Test fun sameSignerWithoutGrantIsRejected() {
  val context = InstrumentationRegistry.getInstrumentation().targetContext
  StorageConnection(context).use { connection ->
   val api = connection.connect().get(20, TimeUnit.SECONDS)
   try { api.query("health", "{}"); fail("Ungrantable caller obtained Storage access") } catch (_: SecurityException) { }
  }
 }
}
