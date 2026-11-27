package com.everyonesoftware.storage.service.pairing

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.FutureTask
import java.util.concurrent.TimeUnit.SECONDS
import java.util.concurrent.atomic.AtomicBoolean
import org.junit.Assert.*
import org.junit.Test

class PairingExchangeTest {
    private val network = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
    private val hostHello = "00112233445566778899AABBCCDDEEFF0157".unhex()
    private val hostPin = ByteArray(32) { 1 }
    private val joinPin = ByteArray(32) { 2 }
    private val nonce = ByteArray(32) { 3 }
    private val secret = ByteArray(32) { 4 }
    private val offer get() = PairingOffer(hostPin, network, Long.MAX_VALUE, secret.copyOf())

    @Test fun qrProofAndBothApprovalsPrecedeLocalSave() = connected { client, host ->
        val acknowledged = AtomicBoolean()
        val peer = exchange(client, offer, confirm = { challenge ->
            assertEquals("W", challenge.peerName)
            assertTrue(challenge.code.matches(Regex("[0-9]{6}")))
            true
        }, save = {
            assertTrue(acknowledged.get())
            assertEquals(PairingPeer("W", hostPin.hex(), network), it)
        }, host = {
            introduce(host, expectsProof = true)
            PairingWire.read(host.getInputStream(), PairingWire.JOIN_APPROVED)
            acknowledged.set(true)
            PairingWire.write(host.getOutputStream(), PairingWire.HOST_SAVED)
            PairingWire.read(host.getInputStream(), PairingWire.JOIN_SAVED)
        })
        assertEquals(network, peer.networkId)
    }

    @Test fun rejectsWrongNetworkBeforeJoinHelloAndOfferProof() = connected { client, host ->
        val error = assertThrows(IOException::class.java) {
            exchange(client, PairingOffer(hostPin, UUID.randomUUID(), Long.MAX_VALUE, secret.copyOf()),
                confirm = { error("unexpected challenge") }, save = { error("unexpected save") }, host = {
                    PairingWire.write(host.getOutputStream(), PairingWire.HOST_HELLO, hostHello)
                    assertEquals(-1, host.getInputStream().read())
                })
        }
        assertFalse(error is PairingUncertain)
    }

    @Test fun rejectsWrongPinBeforeSendingAnyPairingFrame() = connected { client, host ->
        assertThrows(IOException::class.java) {
            exchange(client, PairingOffer(joinPin, network, Long.MAX_VALUE, secret.copyOf()),
                confirm = { error("unexpected challenge") }, save = { error("unexpected save") }, host = {
                    assertEquals(-1, host.getInputStream().read())
                })
        }
    }

    @Test fun refusesToJoinAnotherNetworkEvenWithTheSameKnownHostCertificate() = connected { client, host ->
        assertThrows(IOException::class.java) {
            exchange(client, known = PairingPeer("W", hostPin.hex(), UUID.randomUUID()),
                confirm = { error("unexpected challenge") }, save = { error("unexpected save") }, host = {
                    PairingWire.write(host.getOutputStream(), PairingWire.HOST_HELLO, hostHello)
                    assertEquals(-1, host.getInputStream().read())
                })
        }
    }

    @Test fun rejectsCommitmentMismatchBeforeDisplayingChallenge() = connected { client, host ->
        assertThrows(IOException::class.java) {
            exchange(client, confirm = { error("unexpected challenge") }, save = { error("unexpected save") }, host = {
                val input = host.getInputStream()
                val output = host.getOutputStream()
                PairingWire.write(output, PairingWire.HOST_HELLO, hostHello)
                PairingWire.read(input, PairingWire.JOIN_HELLO, 2, 129)
                PairingWire.write(output, PairingWire.HOST_COMMIT, ByteArray(32))
                PairingWire.read(input, PairingWire.JOIN_COMMIT, 32)
                PairingWire.write(output, PairingWire.HOST_REVEAL, nonce)
                assertEquals(-1, input.read())
            })
        }
    }

    @Test fun localRejectionDoesNotSaveTrust() = connected { client, host ->
        assertThrows(PairingDeclined::class.java) {
            exchange(client, confirm = { false }, save = { error("unexpected save") }, host = {
                introduce(host)
                PairingWire.read(host.getInputStream(), PairingWire.REJECTED)
            })
        }
    }

    @Test fun disconnectAfterApprovalIsUncertainAndDoesNotSaveTrust() = connected { client, host ->
        assertThrows(PairingUncertain::class.java) {
            exchange(client, confirm = { true }, save = { error("unexpected save") }, host = {
                introduce(host)
                PairingWire.read(host.getInputStream(), PairingWire.JOIN_APPROVED)
            })
        }
    }

    @Test fun failedLocalTrustWriteAfterHostAcknowledgmentIsUncertain() = connected { client, host ->
        assertThrows(PairingUncertain::class.java) {
            exchange(client, confirm = { true }, save = { throw IOException("synthetic read-only trust store") }, host = {
                introduce(host)
                PairingWire.read(host.getInputStream(), PairingWire.JOIN_APPROVED)
                PairingWire.write(host.getOutputStream(), PairingWire.HOST_SAVED)
                assertEquals(-1, host.getInputStream().read())
            })
        }
    }

    @Test fun readerDetectsPeerRejectionDuringPendingApproval() = connected { client, host ->
        val challenged = CountDownLatch(1)
        assertThrows(PairingDeclined::class.java) {
            exchange(client, confirm = {
                challenged.countDown()
                CountDownLatch(1).await(15, SECONDS)
                error("decision was not interrupted")
            }, save = { error("unexpected save") }, host = {
                introduce(host)
                assertTrue(challenged.await(3, SECONDS))
                PairingWire.write(host.getOutputStream(), PairingWire.REJECTED)
            })
        }
    }

    @Test fun cancellationClosesTheInFlightSocketAndPreventsTrust() = connected { client, host ->
        val challenged = CountDownLatch(1)
        val cancel = PairingCancellation()
        assertThrows(CancellationException::class.java) {
            exchange(client, confirm = {
                challenged.countDown()
                CountDownLatch(1).await(15, SECONDS)
                error("decision was not interrupted")
            }, save = { error("unexpected save") }, cancellation = cancel, host = {
                introduce(host)
                assertTrue(challenged.await(3, SECONDS))
                cancel.cancel()
                assertEquals(-1, host.getInputStream().read())
            })
        }
    }

    @Test fun trustRecordIsBoundToLocalIdentityAndRejectsTrailingBytes() {
        val peer = PairingPeer("Windows 📁", hostPin.hex(), network)
        val encoded = ByteArrayOutputStream().also { PairingTrustRecord.write(it, joinPin, peer) }.toByteArray()
        assertEquals(peer, PairingTrustRecord.read(ByteArrayInputStream(encoded), joinPin))
        assertThrows(IOException::class.java) { PairingTrustRecord.read(ByteArrayInputStream(encoded), hostPin) }
        assertThrows(IOException::class.java) { PairingTrustRecord.read(ByteArrayInputStream(encoded + 0.toByte()), joinPin) }
    }

    private fun introduce(host: Socket, expectsProof: Boolean = false) {
        val input = host.getInputStream()
        val output = host.getOutputStream()
        PairingWire.write(output, PairingWire.HOST_HELLO, hostHello)
        val joinHello = PairingWire.read(input, PairingWire.JOIN_HELLO, 2, 129)
        if (expectsProof) assertTrue(secret.contentEquals(PairingWire.read(input, PairingWire.JOIN_OFFER_PROOF, 32)))
        val transcript = PairingWire.transcript(hostPin, joinPin, hostHello, joinHello)
        PairingWire.write(output, PairingWire.HOST_COMMIT, PairingWire.commit(true, transcript, nonce))
        val joinCommit = PairingWire.read(input, PairingWire.JOIN_COMMIT, 32)
        PairingWire.write(output, PairingWire.HOST_REVEAL, nonce)
        val joinNonce = PairingWire.read(input, PairingWire.JOIN_REVEAL, 32)
        assertArrayEquals(joinCommit, PairingWire.commit(false, transcript, joinNonce))
    }

    private fun exchange(client: Socket, offer: PairingOffer? = null, confirm: (PairingChallenge) -> Boolean,
        save: (PairingPeer) -> Unit, cancellation: PairingCancellation = PairingCancellation(),
        known: PairingPeer? = null, host: () -> Unit): PairingPeer {
        val thread = FutureTask { host() }
        Thread(thread).apply { isDaemon = true; start() }
        try {
            return cancellation.run(client) {
                JoinExchange.run(client.getInputStream(), client.getOutputStream(), hostPin, joinPin, "Android",
                    offer, known, cancellation, confirm, save)
            }
        } finally {
            client.close()
            thread.get(5, SECONDS)
        }
    }

    private fun connected(test: (Socket, Socket) -> Unit) = ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress()).use { listener ->
        Socket(listener.inetAddress, listener.localPort).use { client ->
            listener.accept().use { host ->
                client.soTimeout = 5000
                host.soTimeout = 5000
                test(client, host)
            }
        }
    }
}
