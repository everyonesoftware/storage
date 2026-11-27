package com.everyonesoftware.storage.service.pairing

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.InetAddress
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test

class PairingWireTest {
    private val network = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
    private val now = 1_900_000_000L
    private val pin = "AB".repeat(32)
    private val secret = "CD".repeat(32)
    private val invitation = "storage-pair-v1:10.0.2.2:$pin:00112233445566778899aabbccddeeff:${now + 60}:$secret"

    @Test fun parsesPinnedOfferWithoutPrintingIt() {
        val input = PairingTarget.parse(invitation, now)
        assertEquals("10.0.2.2", input.host)
        assertEquals(network, input.offer!!.networkId)
        assertEquals(pin, input.offer.fingerprint.hex())
        assertEquals(secret, input.offer.secret.hex())
        assertFalse(input.toString().contains(secret))
        assertFalse(PairingChallenge("test", "012345").toString().contains("012345"))
    }

    @Test fun rejectsExpiredOrNoncanonicalOffersWithoutManualFallback() {
        for (bad in listOf(invitation.replace("10.0.2.2", "10.0.02.2"),
            invitation.replace(pin, pin.lowercase()), invitation.replace(secret, secret.lowercase()),
            invitation.replace("aabb", "AABB"), invitation.replace((now + 60).toString(), now.toString()),
            invitation.replace("${now + 60}", "${now + 181}"),
            invitation.replace("10.0.2.2", "8.8.8.8"), invitation.replace("${now + 60}", "0${now + 60}"),
            invitation + "\n", invitation.replace("storage-pair-v1", "storage-pair-v2"))) {
            val error = assertThrows(IOException::class.java) { PairingTarget.parse(bad, now) }
            assertFalse(error.message.orEmpty().contains(secret))
        }
    }

    @Test fun manualEndpointsUseFixedPortAndRejectOtherAddressSyntax() {
        assertEquals("pc.local", PairingTarget.parse("pc.local", now).host)
        assertEquals("10.0.2.2", PairingTarget.parse("10.0.2.2:48553", now).host)
        for (bad in listOf("10.0.02.2", "0xA000202", "127.1", "[::1]", "http://pc.local", "pc.local:80", "pc/", "\u00a0pc")) {
            assertThrows(IOException::class.java) { PairingTarget.parse(bad, now) }
        }
    }

    @Test fun acceptsOnlyUsableOnLinkPrivateHosts() {
        fun ip(value: String) = InetAddress.getByName(value)
        val local = ip("10.0.2.15")
        assertTrue(PairingLan.contains(local, 24, ip("10.0.2.2")))
        assertTrue(PairingLan.contains(ip("192.168.2.0"), 31, ip("192.168.2.1")))
        for (bad in listOf("10.0.2.0", "10.0.2.255", "10.0.2.15", "10.0.3.2", "8.8.8.8", "127.0.0.1", "::1")) {
            assertFalse(PairingLan.contains(local, 24, ip(bad)))
        }
        assertFalse(PairingLan.contains(local, 7, ip("10.0.2.2")))
    }

    @Test fun windowsTranscriptFixtureKeepsRfc4122UuidAndUnsignedSas() {
        val host = "00112233445566778899AABBCCDDEEFF0157".unhex()
        val join = PairingWire.namePayload("A")
        assertEquals(network to "W", PairingWire.hostHello(host))
        // Expected values independently calculated from the Windows byte layout.
        val transcript = PairingWire.transcript(ByteArray(32) { 1 }, ByteArray(32) { 2 }, host, join)
        assertEquals("5219E07B64577D2F29677353AB9CF973FB5404E1B575BA788903202F6D51A6EE", transcript.hex())
        assertEquals("13807B2C00F6DEF1B4EC233F4669FAF50930FFF8D22BB36DA46B288CAC504994",
            PairingWire.commit(true, transcript, ByteArray(32) { 3 }).hex())
        assertEquals("249218", PairingWire.code(transcript, ByteArray(32) { 3 }, ByteArray(32) { 4 }))
    }

    @Test fun validatesFrameTypeLengthAndNameBeforeUse() {
        val output = ByteArrayOutputStream()
        PairingWire.write(output, 11, ByteArray(32))
        assertArrayEquals(byteArrayOf(11, 32) + ByteArray(32), output.toByteArray())
        for (bad in listOf(byteArrayOf(3, 31), byteArrayOf(4, 32), byteArrayOf(3, 32), byteArrayOf(3))) {
            assertThrows(IOException::class.java) { PairingWire.read(ByteArrayInputStream(bad), 3, 32, 32) }
        }
        for (bad in listOf("  name", "name\u202e", "name\n", "\ud800", "é".repeat(65))) {
            assertThrows(IOException::class.java) { PairingWire.namePayload(bad) }
        }
        assertEquals("é📱", PairingWire.decodeName(PairingWire.namePayload("é📱")))
        assertThrows(IOException::class.java) { PairingWire.decodeName(byteArrayOf(1, 0xc0.toByte())) }
        assertThrows(IOException::class.java) { PairingWire.hostHello(ByteArray(16) + byteArrayOf(1, 65)) }
    }
}
