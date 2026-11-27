package com.everyonesoftware.storage.service.pairing

import android.annotation.TargetApi
import android.content.Context
import android.os.Build
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.Socket
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.time.Instant
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.Semaphore
import java.util.concurrent.SynchronousQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** Blocking, service-private joiner. Call from a background executor, never Binder or the UI thread. */
internal class AndroidPairing(context: Context) {
    private val trust = PairingTrust(context.applicationContext)

    fun list(): List<PairingPeer> = listOfNotNull(trust.read { DeviceIdentity.load(createIfMissing = false) })

    /** The confirmation callback runs once on a worker and must release its decision wait when cancelled. */
    fun join(input: String, onChallenge: (PairingChallenge) -> Boolean, cancellation: PairingCancellation): PairingPeer {
        if (Build.VERSION.SDK_INT < 29) throw IOException("Device pairing requires Android 10 (API 29) or newer.")
        val target = PairingTarget.parse(input, Instant.now().epochSecond)
        if (!admission.tryAcquire()) throw IOException("Another pairing attempt is in progress.")
        try {
            return Socket(Proxy.NO_PROXY).use { socket ->
                cancellation.run(socket) { connect(socket, target, cancellation, onChallenge) }
            }
        } finally {
            admission.release()
        }
    }

    @TargetApi(29)
    private fun connect(socket: Socket, target: PairingTarget, cancellation: PairingCancellation,
        onChallenge: (PairingChallenge) -> Boolean): PairingPeer {
        val offer = target.offer
        try {
            val stored = list().singleOrNull()
            val knownPin = stored?.fingerprint?.unhex()
            if (offer != null && stored != null && (offer.networkId != stored.networkId ||
                    !MessageDigest.isEqual(offer.fingerprint, knownPin))) {
                throw IOException("This device already belongs to a different Storage network.")
            }
            val (peer, local) = resolve(target, cancellation)
            val identity = DeviceIdentity.load(createIfMissing = stored == null)
            cancellation.check()
            offer?.checkExpiry(Instant.now().epochSecond)
            socket.bind(InetSocketAddress(local, 0))
            socket.connect(InetSocketAddress(peer, PAIRING_PORT), 8_000)
            socket.soTimeout = 12_000
            val candidate = PairingCandidate(offer?.fingerprint ?: knownPin)
            val context = SSLContext.getInstance("TLS").apply { init(arrayOf(identity.keyManager()), arrayOf(candidate), null) }
            // The address is already resolved and scoped; creating TLS cannot trigger another DNS lookup.
            val tls = context.socketFactory.createSocket(socket, peer.hostAddress, PAIRING_PORT, false) as SSLSocket
            try {
                tls.useClientMode = true
                tls.enabledProtocols = tls.supportedProtocols.filter { it == "TLSv1.3" || it == "TLSv1.2" }.toTypedArray()
                val alpn = if (offer == null) PairingWire.MANUAL_ALPN else PairingWire.QR_ALPN
                tls.sslParameters = tls.sslParameters.apply { applicationProtocols = arrayOf(alpn) }
                tls.startHandshake()
                cancellation.check()
                val session = tls.session
                val leaf = session.peerCertificates.firstOrNull() as? X509Certificate
                    ?: throw IOException("Missing pairing host certificate.")
                val localCert = session.localCertificates?.firstOrNull()
                if (tls.applicationProtocol != alpn || localCert == null ||
                    !MessageDigest.isEqual(localCert.encoded, identity.certificate.encoded)) {
                    throw IOException("Pairing requires the selected TLS protocol and both device certificates.")
                }
                val hostPin = sha256(leaf.encoded)
                if (!candidate.matchesAuthenticatedLeaf(hostPin)) throw IOException("Could not verify the pairing host certificate.")
                tls.soTimeout = 0 // the absolute deadline still covers the exchange and local decision
                return JoinExchange.run(tls.inputStream, tls.outputStream, hostPin, identity.fingerprint,
                    deviceName(), offer, stored, cancellation, onChallenge) { trust.save(identity, it) }
            } catch (_: SSLException) {
                throw IOException("Could not establish the required secure pairing connection.")
            } finally {
                // Abort the transport first: waiting for an idle TLS read/close could hang cancellation.
                runCatching { socket.close() }
                runCatching { tls.close() }
            }
        } finally {
            offer?.secret?.fill(0)
        }
    }

    private fun resolve(target: PairingTarget, cancellation: PairingCancellation): Pair<InetAddress, InetAddress> {
        val candidates = target.address?.let { arrayOf(it) } ?: try {
            PairingWork("pairing-dns", resolver) { InetAddress.getAllByName(target.host) }.use { cancellation.await(it.result) }
        } catch (_: RejectedExecutionException) {
            throw IOException("Another pairing address lookup is still finishing.")
        } catch (_: java.net.UnknownHostException) {
            throw IOException("Could not resolve the pairing host name.")
        }
        for (peer in candidates) {
            cancellation.check()
            val local = PairingLan.permittedLocalAddress(peer)
            if (local != null) return peer to local
        }
        throw IOException("The pairing host must be on the same private IPv4 LAN.")
    }

    private fun deviceName(): String = Build.MODEL.orEmpty().trim().let { name ->
        if (runCatching { PairingWire.namePayload(name) }.isSuccess) name else "Android device"
    }

    private companion object {
        val admission = Semaphore(1)
        // A system resolver may not respond to interrupt. Bound abandoned DNS work to one thread.
        val resolver = ThreadPoolExecutor(0, 1, 20, TimeUnit.SECONDS, SynchronousQueue()) {
            Thread(it, "storage-pairing-dns").apply { isDaemon = true }
        }
    }
}

/** Private to the pairing TLS context; never use this trust model for repository traffic. */
internal class PairingCandidate(private val pinned: ByteArray?) : X509TrustManager {
    private var authenticated: ByteArray? = null
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("Pairing transport is client-only.")
    }
    @Synchronized override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val cert = chain?.firstOrNull() ?: throw CertificateException("Missing pairing host certificate.")
        val digest = sha256(cert.encoded)
        val expected = pinned ?: authenticated
        if (expected != null && !MessageDigest.isEqual(digest, expected)) {
            throw CertificateException("Pairing host certificate does not match.")
        }
        authenticated = digest
    }
    @Synchronized fun matchesAuthenticatedLeaf(pin: ByteArray): Boolean =
        authenticated?.let { MessageDigest.isEqual(it, pin) } == true
}
