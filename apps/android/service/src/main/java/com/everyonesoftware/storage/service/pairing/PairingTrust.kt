package com.everyonesoftware.storage.service.pairing

import android.content.Context
import android.util.AtomicFile
import java.io.DataInputStream
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

/** A bounded, binary v1 record. Never contains the invitation, address, or SAS. */
internal object PairingTrustRecord {
    private val magic = byteArrayOf(0x53, 0x50, 0x54, 1)

    fun write(output: OutputStream, localPin: ByteArray, peer: PairingPeer) {
        val peerPin = peer.fingerprint.takeIf(fingerprintPattern::matches)?.unhex()
            ?: throw IOException("Invalid paired device identity.")
        if (localPin.size != 32 || peer.networkId == emptyNetwork || MessageDigest.isEqual(localPin, peerPin)) {
            throw IOException("Invalid paired device identity.")
        }
        val network = ByteBuffer.allocate(16).putLong(peer.networkId.mostSignificantBits)
            .putLong(peer.networkId.leastSignificantBits).array()
        val name = PairingWire.namePayload(peer.name)
        output.write(magic + localPin + peerPin + network + name)
    }

    fun read(input: InputStream, localPin: ByteArray): PairingPeer {
        val data = DataInputStream(input)
        fun bytes(length: Int) = ByteArray(length).also { data.readFully(it) }
        if (!bytes(magic.size).contentEquals(magic) || !MessageDigest.isEqual(bytes(32), localPin)) {
            throw IOException("Stored pairing trust does not match the local identity.")
        }
        val peerPin = bytes(32)
        val network = UUID(data.readLong(), data.readLong())
        val nameSize = data.readUnsignedByte()
        if (nameSize !in 1..128 || network == emptyNetwork || MessageDigest.isEqual(localPin, peerPin)) {
            throw IOException("Invalid stored pairing trust.")
        }
        val name = PairingWire.decodeName(byteArrayOf(nameSize.toByte()) + bytes(nameSize))
        if (data.read() != -1) throw IOException("Invalid stored pairing trust.")
        return PairingPeer(name, peerPin.hex(), network)
    }
}

internal class PairingTrust(context: Context) {
    private val base = File(context.noBackupFilesDir, "storage-pairing-trust-v1")
    private val file = AtomicFile(base)

    fun read(identity: () -> DeviceIdentity): PairingPeer? = synchronized(lock) {
        val stream = try { file.openRead() } catch (e: FileNotFoundException) {
            if (base.exists() || File("$base.bak").exists()) throw IOException("Could not read saved pairing trust.", e)
            return@synchronized null
        }
        stream.use { PairingTrustRecord.read(it, identity().fingerprint) }
    }

    fun save(identity: DeviceIdentity, peer: PairingPeer) = synchronized(lock) {
        val previous = read { identity }
        if (previous != null && (previous.fingerprint != peer.fingerprint || previous.networkId != peer.networkId)) {
            throw IOException("Refusing to replace the trusted coordinator.")
        }
        val stream = file.startWrite()
        try {
            PairingTrustRecord.write(stream, identity.fingerprint, peer)
            stream.fd.sync()
        } catch (e: Exception) {
            file.failWrite(stream)
            throw e
        }
        file.finishWrite(stream)
        // AtomicFile reports certain I/O failures through logging rather than exceptions.
        if (read { identity } != peer) throw IOException("Could not save pairing trust.")
    }

    private companion object { val lock = Any() }
}
