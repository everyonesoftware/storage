package com.everyonesoftware.storage.service

import android.Manifest
import android.accessibilityservice.AccessibilityServiceInfo
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.SystemClock
import android.view.View
import android.view.ViewGroup
import android.view.accessibility.AccessibilityNodeInfo
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.service.pairing.PairingChallenge
import com.everyonesoftware.storage.service.pairing.PairingDeclined
import com.everyonesoftware.storage.service.pairing.PairingPeer
import com.everyonesoftware.storage.service.pairing.PairingUncertain
import com.everyonesoftware.storage.service.pairing.ui.PairingActivity
import com.everyonesoftware.storage.service.pairing.ui.PairingBackend
import com.everyonesoftware.storage.service.pairing.ui.PairingQrDecoder
import com.google.zxing.BarcodeFormat
import com.google.zxing.MultiFormatWriter
import java.io.IOException
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PairingActivityTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val peer = PairingPeer("Fixture PC", "A".repeat(64), UUID(0, 1))
    private val challenge = PairingChallenge(peer.name, "042719")

    @After fun restoreBackend() { PairingActivity.backendFactory = PairingActivity.productionBackend }

    @Test fun launcherOffersCameraAndNativeManualEntry(): Unit = launch().use { scenario ->
        scenario.onActivity { activity ->
            val root = activity.window.decorView
            assertTrue(root.hasText("Storage Service"))
            assertTrue(root.views<Button>().any { it.text.toString() == "Scan QR code" })
            root.button("Enter address manually").performClick()
            val field = root.views<EditText>().single()
            assertEquals("", field.text.toString())
            for (invalid in listOf("", "https://PC.local", syntheticOffer())) {
                field.setText(invalid)
                root.button("Connect").performClick()
                assertEquals("Enter the Windows PC hostname or IP address.", field.error.toString())
            }
        }
    }

    @Test fun explicitApprovalWaitsForWorkerAndReopeningReadsSavedPeer() {
        val requests = LinkedBlockingQueue<String>()
        val decisions = LinkedBlockingQueue<Boolean>()
        val complete = CountDownLatch(1)
        val saved = AtomicReference<List<PairingPeer>>(emptyList())
        fixture(PairingBackend(saved::get) { input, confirm, _ ->
            requests.add(input)
            if (!confirm(challenge).also { decisions.add(it) }) throw PairingDeclined()
            check(complete.await(10, TimeUnit.SECONDS))
            saved.set(listOf(peer))
            peer
        })
        try {
            launch().use { scenario ->
                scenario.enterAddress("  pc.local  ")
                assertEquals("pc.local", requests.poll(10, TimeUnit.SECONDS))
                scenario.awaitText("Do these codes match?")
                scenario.onActivity { activity ->
                    val root = activity.window.decorView
                    assertTrue(root.hasText("0 4 2 7 1 9"))
                    assertTrue(root.views<TextView>().any { it.text.contains(peer.name) })
                    assertNull(decisions.poll())
                    root.button("Codes match").performClick()
                    assertFalse(root.hasText("Device paired."))
                }
                assertEquals(true, decisions.poll(10, TimeUnit.SECONDS))
                complete.countDown()
                scenario.awaitText("Device paired.")
                scenario.awaitText("Fixture PC · Paired Windows PC")
                scenario.recreate()
                scenario.awaitText("Fixture PC · Paired Windows PC")
            }
        } finally { complete.countDown() }
    }

    @Test fun mismatchDeclinesWithoutSaving() {
        val decisions = LinkedBlockingQueue<Boolean>()
        fixture(PairingBackend({ emptyList() }) { _, confirm, _ ->
            if (!confirm(challenge).also { decisions.add(it) }) throw PairingDeclined()
            error("Unexpected approval")
        })
        launch().use { scenario ->
            scenario.enterAddress("pc.local")
            scenario.awaitText("Do these codes match?")
            scenario.click("Codes don’t match")
            assertEquals(false, decisions.poll(10, TimeUnit.SECONDS))
            scenario.awaitText("Pairing was declined. No trust was added by this attempt.")
            scenario.awaitText("No paired Windows PC.")
        }
    }

    @Test fun recreationCancelsDecisionAndIgnoresOldResult() {
        val decisions = LinkedBlockingQueue<Boolean>()
        fixture(PairingBackend({ emptyList() }) { _, confirm, _ ->
            decisions.add(confirm(challenge))
            throw IOException("Late reply")
        })
        launch().use { scenario ->
            scenario.enterAddress("pc.local")
            scenario.awaitText("Do these codes match?")
            scenario.recreate()
            assertEquals(false, decisions.poll(10, TimeUnit.SECONDS))
            scenario.awaitText("No paired Windows PC.")
            scenario.onActivity { activity ->
                val root = activity.window.decorView
                assertFalse(root.hasText("Do these codes match?"))
                assertFalse(root.hasText("Device paired."))
            }
        }
    }

    @Test fun ambiguousSaveShowsUnconfirmedAndRefreshesWithoutRetry() {
        val requests = LinkedBlockingQueue<String>()
        fixture(PairingBackend({ emptyList() }) { input, confirm, _ ->
            requests.add(input)
            check(confirm(challenge))
            throw PairingUncertain(IOException("Synthetic acknowledgement loss"))
        })
        launch().use { scenario ->
            scenario.enterAddress("pc.local")
            scenario.awaitText("Do these codes match?")
            scenario.click("Codes match")
            scenario.awaitText("Pairing could not be confirmed. Check the device lists on both devices before trying again.")
            scenario.recreate()
            scenario.awaitText("Pairing could not be confirmed. Check the device lists on both devices before trying again.")
            scenario.click("Refresh paired devices")
            scenario.awaitText("No paired Windows PC.")
            assertEquals("pc.local", requests.poll(10, TimeUnit.SECONDS))
            assertNull(requests.poll())
        }
    }

    @Test fun deniedOrUnavailableCameraLeavesManualFallback() {
        val context = instrumentation.targetContext
        assertEquals("Revoke camera before running this suite", PackageManager.PERMISSION_DENIED,
            context.checkSelfPermission(Manifest.permission.CAMERA))
        launch().use { scenario ->
            scenario.click("Scan QR code")
            val hasCamera = context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY)
            if (hasCamera) {
                val automation = instrumentation.uiAutomation
                automation.serviceInfo = automation.serviceInfo.apply {
                    flags = flags or AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS
                }
                waitFor {
                    val root = automation.rootInActiveWindow ?: return@waitFor false
                    root.findNodes().firstOrNull { it.viewIdResourceName?.endsWith(":id/permission_deny_button") == true }
                        ?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true
                }
            }
            scenario.awaitText(if (hasCamera) "Camera access wasn’t granted. You can still enter the PC’s address manually."
                else "The camera is unavailable. You can still enter the PC’s address manually.")
            scenario.click("Enter address manually")
            scenario.awaitText("Enter the PC’s address")
        }
    }

    @Test fun realQrDecoderReadsFixtureWithCameraRowPadding() {
        val payload = syntheticOffer()
        val size = 320
        val rowStride = size + 13
        val pixels = ByteBuffer.allocate(rowStride * size)
        val qr = MultiFormatWriter().encode(payload, BarcodeFormat.QR_CODE, size, size)
        for (y in 0 until size) for (x in 0 until size) {
            pixels.put(y * rowStride + x, if (qr[x, y]) 0 else -1)
        }
        assertEquals(payload, PairingQrDecoder().decode(pixels, size, size, rowStride, 1))
        pixels.limit(64)
        assertNull(PairingQrDecoder().decode(pixels, size, size, rowStride, 1))
    }

    private fun fixture(backend: PairingBackend) { PairingActivity.backendFactory = { backend } }

    private fun launch(): ActivityScenario<Activity> {
        val context = instrumentation.targetContext
        val intent = context.packageManager.getLaunchIntentForPackage(context.packageName)
        assertNotNull("Storage Service must have a launcher activity", intent)
        assertEquals(Intent.ACTION_MAIN, intent!!.action)
        return ActivityScenario.launch(intent.apply { putExtra("invitation", "must-not-be-used.example") })
    }

    private fun ActivityScenario<Activity>.enterAddress(address: String) {
        click("Enter address manually")
        onActivity { activity ->
            activity.window.decorView.views<EditText>().single().setText(address)
        }
        click("Connect")
    }

    private fun ActivityScenario<Activity>.click(label: String) = onActivity { it.window.decorView.button(label).performClick() }

    private fun ActivityScenario<Activity>.awaitText(text: String) = waitFor {
        var found = false
        onActivity { found = it.window.decorView.hasText(text) }
        found
    }

    private fun waitFor(condition: () -> Boolean) {
        val end = SystemClock.elapsedRealtime() + 15_000
        while (SystemClock.elapsedRealtime() < end) {
            if (condition()) return
            SystemClock.sleep(100)
        }
        throw AssertionError("Timed out waiting for pairing UI state")
    }

    private fun syntheticOffer() = "storage-pair-v1:192.168.1.2:${"A".repeat(64)}:${"1".repeat(32)}:2000000000:${"B".repeat(64)}"
}

private inline fun <reified T : View> View.views(): List<T> = descendants().filterIsInstance<T>().toList()
private fun View.descendants(): Sequence<View> = sequence {
    yield(this@descendants)
    if (this@descendants is ViewGroup) for (index in 0 until childCount) yieldAll(getChildAt(index).descendants())
}
private fun View.button(label: String): Button = views<Button>().single { it.text.toString() == label }
private fun View.hasText(label: String) = views<TextView>().any { it.text.toString() == label }
private fun AccessibilityNodeInfo.findNodes(): Sequence<AccessibilityNodeInfo> = sequence {
    yield(this@findNodes)
    for (index in 0 until childCount) getChild(index)?.let { yieldAll(it.findNodes()) }
}
