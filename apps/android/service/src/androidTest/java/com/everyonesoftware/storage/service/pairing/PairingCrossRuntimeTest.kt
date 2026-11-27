package com.everyonesoftware.storage.service.pairing

import android.os.Build
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Opt-in: select this class with -e pairingContract true -e expectedHost 'Fixture PC'.
 * Supply -e pairingEndpoint 10.0.2.2:48553 for the synthetic manual host, or stream the
 * synthetic QR payload into files/pairing-fixture-input using run-as on the disposable Service APK.
 * Use -e pairingReadOnly true after a force-stop to verify the same durable trust in a new process.
 */
class PairingCrossRuntimeTest {
    @Test fun joinsAndPersistsWindowsCoordinator() {
        val args = InstrumentationRegistry.getArguments()
        assumeTrue("An explicitly configured external Windows protocol host is required.", args.getString("pairingContract") == "true")
        assertTrue("The interoperability fixture requires API 29 or newer.", Build.VERSION.SDK_INT >= 29)
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val expectedName = args.getString("expectedHost") ?: error("Supply the synthetic host display name.")
        val pairing = AndroidPairing(context)
        if (args.getString("pairingReadOnly") == "true") {
            val durable = pairing.list()
            assertTrue("Saved coordinator did not survive restart.", durable.size == 1 && durable.single().name == expectedName)
            return
        }
        val manual = args.getString("pairingEndpoint")
        val input = if (manual != null) {
            assertTrue("The endpoint argument is manual only; put the offer in the private fixture file.",
                !manual.startsWith("storage-pair-", ignoreCase = true))
            manual
        } else {
            val fixture = File(context.filesDir, "pairing-fixture-input")
            try {
                fixture.inputStream().buffered().use { stream ->
                    val bounded = ByteArray(260)
                    var used = 0
                    while (used < bounded.size) {
                        val read = stream.read(bounded, used, bounded.size - used)
                        if (read < 0) break
                        used += read
                    }
                    assertTrue("Invalid synthetic offer length.", used in 1..259)
                    String(bounded, 0, used, Charsets.US_ASCII)
                }
            } finally {
                fixture.delete()
            }
        }
        val challengeCount = AtomicInteger()
        val joined = pairing.join(input, { challenge ->
            assertTrue("Received multiple confirmation prompts.", challengeCount.incrementAndGet() == 1)
            assertTrue("Unexpected host display name.", challenge.peerName == expectedName)
            assertTrue("Expected six ASCII digits.", challenge.code.matches(Regex("[0-9]{6}")))
            true // Synthetic host and challenge only; never auto-approve production joins.
        }, PairingCancellation())
        assertTrue("Join completed without exactly one challenge.", challengeCount.get() == 1)
        assertTrue("Joined coordinator was not read back from durable trust.", joined.name == expectedName &&
            AndroidPairing(context).list() == listOf(joined))
        val existing = DeviceIdentity.load(createIfMissing = false)
        assertTrue("Pairing did not reopen the same enrollment certificate.", existing.fingerprint.contentEquals(
            DeviceIdentity.load(createIfMissing = false).fingerprint))
    }
}
