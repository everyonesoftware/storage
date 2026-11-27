package com.everyonesoftware.storage.service.pairing

import java.io.DataInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CharacterCodingException
import java.security.MessageDigest
import java.util.UUID

internal data class PairingPeer(val name: String, val fingerprint: String, val networkId: UUID)
internal data class PairingChallenge(val peerName: String, val code: String) {
    override fun toString(): String = "PairingChallenge(redacted)"
}
internal class PairingDeclined : IOException("Pairing was declined.")
internal class PairingUncertain(cause: Throwable) : IOException(
    "Pairing could not be confirmed. Check the paired device lists on both devices before retrying.", cause,
)

internal val emptyNetwork = UUID(0, 0)
internal val fingerprintPattern = Regex("[0-9A-F]{64}")
private val hexDigits = "0123456789ABCDEF"
internal fun ByteArray.hex(): String = buildString(size * 2) {
    for (byte in this@hex) {
        append(hexDigits[(byte.toInt() and 255) ushr 4])
        append(hexDigits[byte.toInt() and 15])
    }
}
internal fun String.unhex(): ByteArray = ByteArray(length / 2) { index ->
    ((digitToIntAt(index * 2) shl 4) or digitToIntAt(index * 2 + 1)).toByte()
}
private fun String.digitToIntAt(index: Int): Int = this[index].digitToInt(16)
internal fun sha256(vararg parts: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").run {
    parts.forEach { update(it) }
    digest()
}

internal object PairingWire {
    const val HOST_HELLO = 1
    const val JOIN_HELLO = 2
    const val HOST_COMMIT = 3
    const val JOIN_COMMIT = 4
    const val HOST_REVEAL = 5
    const val JOIN_REVEAL = 6
    const val JOIN_APPROVED = 7
    const val HOST_SAVED = 8
    const val JOIN_SAVED = 9
    const val REJECTED = 10
    const val JOIN_OFFER_PROOF = 11
    const val DIGEST_BYTES = 32
    const val MIN_HOST_HELLO = 18
    const val MAX_HOST_HELLO = 145
    const val MANUAL_ALPN = "storage-pairing/1"
    const val QR_ALPN = "storage-pairing-qr/1"
    private const val MAX_NAME_BYTES = 128

    private fun domain(label: String) = "$MANUAL_ALPN $label\u0000".toByteArray(Charsets.US_ASCII)

    fun transcript(hostPin: ByteArray, joinPin: ByteArray, hostHello: ByteArray, joinHello: ByteArray) =
        sha256(domain("transcript"), hostPin, joinPin, hostHello, joinHello)

    fun commit(host: Boolean, transcript: ByteArray, nonce: ByteArray) =
        sha256(domain("commitment"), byteArrayOf(if (host) 1 else 2), transcript, nonce)

    fun code(transcript: ByteArray, hostNonce: ByteArray, joinNonce: ByteArray): String {
        val hash = sha256(domain("code"), transcript, hostNonce, joinNonce)
        val head = ByteBuffer.wrap(hash).long.toULong()
        return (head % 1_000_000UL).toString().padStart(6, '0')
    }

    fun hostHello(payload: ByteArray): Pair<UUID, String> {
        if (payload.size !in MIN_HOST_HELLO..MAX_HOST_HELLO) throw IOException("Invalid host identity.")
        val bytes = ByteBuffer.wrap(payload)
        val network = UUID(bytes.long, bytes.long)
        if (network == emptyNetwork) throw IOException("Missing pairing network identity.")
        return network to decodeName(payload.copyOfRange(16, payload.size))
    }

    fun namePayload(name: String): ByteArray {
        if (name.length > MAX_NAME_BYTES) throw IOException("Invalid pairing device name.")
        validateName(name)
        val encoded = try { Charsets.UTF_8.newEncoder().encode(CharBuffer.wrap(name)) }
        catch (_: CharacterCodingException) { throw IOException("Invalid pairing device name.") }
        val length = encoded.remaining()
        if (length !in 1..MAX_NAME_BYTES) throw IOException("Invalid pairing device name.")
        return ByteArray(length + 1).also {
            it[0] = length.toByte()
            encoded.get(it, 1, length)
        }
    }

    fun decodeName(bytes: ByteArray): String {
        if (bytes.size !in 2..MAX_NAME_BYTES + 1 || (bytes[0].toInt() and 255) != bytes.size - 1) {
            throw IOException("Invalid pairing device name length.")
        }
        val name = try { Charsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes, 1, bytes.size - 1)).toString() }
        catch (_: CharacterCodingException) { throw IOException("Invalid pairing device name encoding.") }
        validateName(name)
        return name
    }

    private fun validateName(name: String) {
        fun space(ch: Char) = ch.isWhitespace() || ch == '\u0085' // .NET Unicode whitespace
        if (name.isEmpty() || space(name.first()) || space(name.last())) throw IOException("Invalid pairing device name.")
        var position = 0
        while (position < name.length) {
            val rune = name.codePointAt(position)
            when (Character.getType(rune)) {
                Character.CONTROL.toInt(), Character.FORMAT.toInt(), Character.LINE_SEPARATOR.toInt(),
                Character.PARAGRAPH_SEPARATOR.toInt() -> throw IOException("Invalid pairing device name.")
            }
            position += Character.charCount(rune)
        }
    }

    fun write(output: OutputStream, frame: Int, payload: ByteArray = ByteArray(0)) {
        require(frame in 1..JOIN_OFFER_PROOF && payload.size <= 255)
        output.write(byteArrayOf(frame.toByte(), payload.size.toByte()) + payload)
        output.flush()
    }

    fun read(input: InputStream, expected: Int, minimum: Int = 0, maximum: Int = minimum): ByteArray {
        val frame = DataInputStream(input)
        val type = frame.readUnsignedByte()
        val size = frame.readUnsignedByte()
        if (type == REJECTED && size == 0 && expected == HOST_SAVED) throw PairingDeclined()
        if (type != expected || size !in minimum..maximum) throw IOException("Unexpected pairing message type or size.")
        return ByteArray(size).also { frame.readFully(it) }
    }
}
