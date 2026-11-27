package com.everyonesoftware.storage.service.pairing

import java.io.IOException
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.nio.ByteBuffer
import java.util.Collections
import java.util.UUID

internal const val PAIRING_PORT = 48553
private const val OFFER_PREFIX = "storage-pair-v1:"
private const val OFFER_WINDOW_SECONDS = 180L
private val canonicalIpv4 = Regex("(?:0|[1-9][0-9]{0,2})(?:\\.(?:0|[1-9][0-9]{0,2})){3}")
private val uuidN = Regex("[0-9a-f]{32}")
private val unixSeconds = Regex("[1-9][0-9]{0,18}")
private val label = Regex("[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?")
private val numericComponent = Regex("(?:[0-9]+|0[xX][0-9a-fA-F]+)")

internal class PairingOffer(val fingerprint: ByteArray, val networkId: UUID, private val expiry: Long, val secret: ByteArray) {
    fun checkExpiry(now: Long) {
        if (now >= expiry) throw IOException("The pairing invitation has expired.")
    }
}

internal class PairingTarget private constructor(val host: String, val address: InetAddress?, val offer: PairingOffer?) {
    companion object {
        fun parse(input: String, now: Long): PairingTarget {
            if (input.length !in 1..259 || input.any { it !in '!'..'~' }) throw invalid()
            if (input.startsWith(OFFER_PREFIX)) {
                val fields = input.split(':')
                if (fields.size != 6 || !fingerprintPattern.matches(fields[2]) || !uuidN.matches(fields[3]) ||
                    !unixSeconds.matches(fields[4]) || !fingerprintPattern.matches(fields[5])) throw invalid()
                val ip = literal(fields[1]) ?: throw invalid()
                if (PairingLan.minimumPrefix(ip) > 31) throw invalid()
                val bytes = ByteBuffer.wrap(fields[3].unhex())
                val network = UUID(bytes.long, bytes.long)
                if (network == emptyNetwork) throw invalid()
                val expiry = fields[4].toLongOrNull() ?: throw invalid()
                if (expiry <= now) throw IOException("The pairing invitation has expired.")
                if (now < expiry - OFFER_WINDOW_SECONDS) throw invalid()
                val offer = PairingOffer(fields[2].unhex(), network, expiry, fields[5].unhex())
                return PairingTarget(fields[1], ip, offer)
            }
            val host = input.removeSuffix(":$PAIRING_PORT")
            if (host.startsWith("storage-pair-", ignoreCase = true)) throw invalid()
            val ip = literal(host)
            if (ip != null) {
                if (PairingLan.minimumPrefix(ip) > 31) throw invalid()
            } else {
                val labels = host.split('.')
                if (host.length > 253 || labels.any { !label.matches(it) } || labels.all { numericComponent.matches(it) }) {
                    throw invalid()
                }
            }
            return PairingTarget(host, ip, null)
        }

        private fun invalid() = IOException("Invalid pairing invitation or endpoint.")

        private fun literal(host: String): InetAddress? {
            if (!canonicalIpv4.matches(host)) return null
            val octets = host.split('.').map { it.toInt() }
            if (octets.any { it > 255 }) return null
            return InetAddress.getByAddress(octets.map { it.toByte() }.toByteArray())
        }
    }
}

internal object PairingLan {
    private fun number(ip: InetAddress) = ByteBuffer.wrap(ip.address).int.toUInt()

    fun minimumPrefix(ip: InetAddress): Int {
        if (ip !is Inet4Address) return 33
        val address = number(ip)
        return when (address) {
            in 0x0a000000U..0x0affffffU -> 8
            in 0xac100000U..0xac1fffffU -> 12
            in 0xc0a80000U..0xc0a8ffffU, in 0xa9fe0000U..0xa9feffffU -> 16
            else -> 33
        }
    }

    fun contains(local: InetAddress, prefix: Int, peer: InetAddress): Boolean {
        if (prefix !in minimumPrefix(local)..31 || peer !is Inet4Address || local == peer) return false
        val mask = UInt.MAX_VALUE shl (32 - prefix)
        val network = number(local) and mask
        val broadcast = network or mask.inv()
        fun usable(address: UInt) = (address and mask) == network &&
            (prefix == 31 || (address != network && address != broadcast))
        return usable(number(local)) && usable(number(peer))
    }

    fun permittedLocalAddress(peer: InetAddress): InetAddress? {
        val interfaces = NetworkInterface.getNetworkInterfaces() ?: return null
        val addresses = Collections.list(interfaces).asSequence()
            .filter { it.isUp && !it.isLoopback && !it.isPointToPoint && !it.isVirtual }
            .flatMap { it.interfaceAddresses.asSequence() }.toList()
        if (addresses.any { it.address == peer }) return null
        return addresses.filter { contains(it.address, it.networkPrefixLength.toInt(), peer) }
            .maxByOrNull { it.networkPrefixLength }?.address
    }
}
