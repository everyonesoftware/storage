package com.everyonesoftware.storage.service.pairing

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.IOException
import java.math.BigInteger
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Date
import javax.net.ssl.X509KeyManager
import javax.security.auth.x500.X500Principal

internal class DeviceIdentity private constructor(val certificate: X509Certificate, private val key: PrivateKey) {
    val fingerprint: ByteArray = sha256(certificate.encoded)

    // An empty/unrelated accepted-issuer list is normal for this enrollment protocol.
    // There is only one local TLS identity, selected for RSA client authentication.
    fun keyManager(): X509KeyManager = object : X509KeyManager {
        override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? =
            if (keyType == "RSA") arrayOf(alias) else null
        override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?): String? =
            if (keyType?.contains("RSA") == true) alias else null
        override fun getCertificateChain(name: String?): Array<X509Certificate>? = if (name == alias) arrayOf(certificate) else null
        override fun getPrivateKey(name: String?): PrivateKey? = if (name == alias) key else null
        override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null
        override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = null
    }

    companion object {
        private const val provider = "AndroidKeyStore"
        private const val alias = "storage-pairing-identity-v1"

        @Synchronized fun load(createIfMissing: Boolean): DeviceIdentity = try {
            val store = KeyStore.getInstance(provider).apply { load(null) }
            if (!store.containsAlias(alias)) {
                if (!createIfMissing) throw IOException("The saved pairing identity is no longer available.")
                val now = System.currentTimeMillis()
                val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_RSA, provider)
                generator.initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                    .setKeySize(2048)
                    .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256,
                        KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                    .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1, KeyProperties.SIGNATURE_PADDING_RSA_PSS)
                    // Conscrypt can request raw RSA signing for an opaque AndroidKeyStore private key.
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setCertificateSubject(X500Principal("CN=Storage Android Pairing Device"))
                    .setCertificateSerialNumber(BigInteger(128, SecureRandom()).setBit(0))
                    .setCertificateNotBefore(Date(now - 86_400_000L))
                    .setCertificateNotAfter(Date(now + 946_728_000_000L))
                    .build())
                generator.generateKeyPair()
            }
            val cert = store.getCertificate(alias) as? X509Certificate ?: throw IOException("Missing pairing certificate.")
            val key = store.getKey(alias, null) as? PrivateKey ?: throw IOException("Missing pairing private key.")
            if (key.algorithm != "RSA" || key.encoded != null || cert.publicKey.algorithm != "RSA") {
                throw IOException("Invalid local pairing identity.")
            }
            DeviceIdentity(cert, key)
        } catch (e: IOException) {
            throw e
        } catch (_: Exception) {
            throw IOException("Could not open the device's protected pairing identity.")
        }
    }
}
