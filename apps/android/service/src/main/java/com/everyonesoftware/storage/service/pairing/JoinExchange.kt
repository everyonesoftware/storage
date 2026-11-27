package com.everyonesoftware.storage.service.pairing

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.util.concurrent.CancellationException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executor
import java.util.concurrent.FutureTask
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

internal object JoinExchange {
    private val random = SecureRandom()

    /** Streams are usable here only after the outer transport verifies mutual TLS and ALPN. */
    fun run(input: InputStream, output: OutputStream, hostPin: ByteArray, localPin: ByteArray,
        localName: String, offer: PairingOffer?, expectedNetwork: PairingPeer?, cancellation: PairingCancellation,
        confirm: (PairingChallenge) -> Boolean, save: (PairingPeer) -> Unit): PairingPeer {
        with(PairingWire) {
            cancellation.check()
            if (MessageDigest.isEqual(hostPin, localPin)) throw IOException("Cannot pair a device with itself.")
            if (offer != null && !MessageDigest.isEqual(offer.fingerprint, hostPin)) {
                throw IOException("The pairing invitation does not match this host.")
            }
            val hostHello = read(input, HOST_HELLO, MIN_HOST_HELLO, MAX_HOST_HELLO)
            val (network, name) = hostHello(hostHello)
            if (offer != null && offer.networkId != network) throw IOException("The pairing invitation does not match this network.")
            if (expectedNetwork != null && (expectedNetwork.networkId != network || expectedNetwork.fingerprint != hostPin.hex())) {
                throw IOException("This device already belongs to a different Storage network.")
            }
            val joinHello = namePayload(localName)
            write(output, JOIN_HELLO, joinHello)
            if (offer != null) {
                cancellation.check()
                offer.checkExpiry(Instant.now().epochSecond)
                write(output, JOIN_OFFER_PROOF, offer.secret)
            }
            val transcript = transcript(hostPin, localPin, hostHello, joinHello)
            val nonce = ByteArray(DIGEST_BYTES).also { random.nextBytes(it) }
            val hostCommit = read(input, HOST_COMMIT, DIGEST_BYTES)
            write(output, JOIN_COMMIT, commit(false, transcript, nonce))
            val hostNonce = read(input, HOST_REVEAL, DIGEST_BYTES)
            if (!MessageDigest.isEqual(hostCommit, commit(true, transcript, hostNonce))) {
                throw IOException("The pairing host did not honor its commitment.")
            }
            write(output, JOIN_REVEAL, nonce)
            val peer = PairingPeer(name, hostPin.hex(), network)
            approve(input, output, PairingChallenge(name, code(transcript, hostNonce, nonce)), peer,
                cancellation, confirm, save)
            return peer
        }
    }

    private fun approve(input: InputStream, output: OutputStream, challenge: PairingChallenge, peer: PairingPeer,
        cancellation: PairingCancellation, confirm: (PairingChallenge) -> Boolean, save: (PairingPeer) -> Unit) {
        val sendingApproval = AtomicBoolean()
        val remote = PairingWork("pairing-host-decision") {
            PairingWire.read(input, PairingWire.HOST_SAVED)
            if (!sendingApproval.get()) throw IOException("The host claimed trust before join approval.")
        }
        val decision = PairingWork("pairing-local-decision") { cancellation.check(); confirm(challenge) }
        try {
            cancellation.awaitEither(decision.result, remote.result)
            // A rejection, disconnect or early ack should retire the pending local prompt.
            if (remote.result.isDone) cancellation.await(remote.result)
            if (!cancellation.await(decision.result)) {
                PairingWire.write(output, PairingWire.REJECTED)
                throw PairingDeclined()
            }
            cancellation.check()
            try {
                // After this write starts, cancellation cannot prove that the host did not save.
                sendingApproval.set(true)
                PairingWire.write(output, PairingWire.JOIN_APPROVED)
                cancellation.await(remote.result)
                cancellation.check()
                save(peer)
                cancellation.check()
                PairingWire.write(output, PairingWire.JOIN_SAVED)
            } catch (e: PairingDeclined) {
                throw e
            } catch (e: IOException) {
                throw PairingUncertain(e)
            } catch (e: CancellationException) {
                throw PairingUncertain(e)
            } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
                throw PairingUncertain(e)
            }
        } finally {
            decision.close()
            remote.close()
        }
    }
}

internal class PairingWork<T>(name: String, executor: Executor? = null, action: () -> T) : AutoCloseable {
    val result = CompletableFuture<T>()
    private val task = FutureTask {
        try { result.complete(action()) }
        catch (e: Throwable) { result.completeExceptionally(e) }
    }
    init {
        if (executor == null) Thread(task, name).apply { isDaemon = true; start() }
        else executor.execute(task)
    }
    override fun close() { task.cancel(true) }
}

// Single-use capability. Close the raw socket (not SSLSocket) to interrupt even a blocked handshake.
internal class PairingCancellation {
    private val socket = AtomicReference<Socket?>()
    private val started = AtomicBoolean()
    private val stopped = CompletableFuture<Throwable>()

    fun cancel() = stop(CancellationException("Pairing was cancelled."))
    private fun stop(reason: Throwable) {
        if (stopped.complete(reason)) socket.get()?.let { runCatching { it.close() } }
    }
    fun check() { stopped.getNow(null)?.let { throw it } }

    private fun <T> get(future: CompletableFuture<T>): T = try { future.get() }
    catch (e: ExecutionException) { throw (e.cause ?: e) }

    fun awaitEither(first: CompletableFuture<*>, second: CompletableFuture<*>) {
        get(CompletableFuture.anyOf(first, second, stopped))
        check()
    }

    fun <T> await(future: CompletableFuture<T>): T {
        get(CompletableFuture.anyOf(future, stopped))
        check()
        return get(future)
    }

    fun <T> run(connection: Socket, exchange: () -> T): T {
        check(started.compareAndSet(false, true)) { "Pairing cancellation cannot be reused." }
        socket.set(connection)
        val deadline = timer.schedule({ stop(SocketTimeoutException("Pairing timed out.")) }, 180, TimeUnit.SECONDS)
        try {
            check()
            return exchange()
        } catch (e: IOException) {
            if (e !is PairingUncertain) check()
            throw e
        } finally {
            deadline.cancel(false)
            socket.set(null)
            runCatching { connection.close() }
        }
    }

    private companion object {
        val timer = ScheduledThreadPoolExecutor(1) { Thread(it, "pairing-deadline").apply { isDaemon = true } }
            .apply { removeOnCancelPolicy = true }
    }
}
