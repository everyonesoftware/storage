# Service-private device enrollment

Scope: Windows coordinator, Windows or Android 10+ joiner. This contract enrolls device trust only. It does not extend Browser IPC, share folders, synchronize catalog entries or transfer file contents. Android cannot host a network in this slice. Keep credentials and private keys within Storage Service.

## Entry paths

- **Windows → Windows**: the host chooses **Add device**. A joiner can select a nearby host or type its local address. UDP discovery is a hint. Use the original `storage-pairing/1` TLS ALPN and fixed TCP port 48553.
- **Windows → Android, scan**: the host chooses **Add Android phone**. The phone scans in Storage Service. A QR-only offer is not advertised over UDP, and it never accepts the manual-pairing ALPN.
- **Windows → Android, manual**: the host chooses **Add device**. The phone types an address. Use the original manual ALPN and the same comparison/approval screens. Unsuccessful QR parsing, authentication, or negotiation must not retry via the manual mode.

## QR offer

The QR carries exactly the ASCII string `storage-pair-v1:<ipv4>:<fingerprint>:<network>:<expiry>:<secret>`:

| Field | Encoding |
| --- | --- |
| ipv4 | Canonical dotted decimal IPv4 on a supported directly attached private or link-local network; no port override |
| fingerprint | SHA-256 of the exact host leaf certificate's DER, 64 uppercase hexadecimal digits |
| network | Coordinator UUID, 32 lowercase hexadecimal digits, no hyphens |
| expiry | Expiry expressed in decimal Unix seconds; at most the short enrollment window |
| secret | 32 random bytes, 64 uppercase hexadecimal digits |

Only `storage-pairing-qr/1` applies. Android verifies the enrolled candidate's certificate fingerprint during TLS, before sending the secret, and compares the coordinator UUID with HostHello. The host accepts at most one connection for the explicit offer. It closes the offer on cancellation, expiry, use or interrupted attempt. QR payloads and secrets must not be placed in logs, Binder outputs, saved instance state, screenshots used as evidence, or durable preferences. If the devices are on different networks or inbound pairing is blocked, a correct QR code does not provide reachability.

## Encrypted exchange

The authoritative manual wire implementation is `apps/windows/Storage.Service/Pairing/PairingProtocol.cs`. Both sides prove possession of their respective candidate device keys in encrypted TLS; the connection is not treated as a trusted peer at this point. The manual and QR versions share the same bounded framing: one-byte type, one-byte length, then the indicated number of bytes. HostHello includes the 16-byte big-endian UUID and a length-prefixed UTF-8 host name; JoinHello contains a length-prefixed UTF-8 joiner name. For QR only, the joiner then sends frame 11 with the 32-byte raw secret; the host validates it in constant time and rejects invalid or expired offers before entering the normal commitment exchange. In either mode, ordered certificate digests, names, network ID and fresh committed random values bind the identical six-digit confirmation code to this exchange. It is not an invitation or an authentication secret typed into a form.

Both service UIs identify the peer and require an explicit positive decision after the person compares the codes. A mismatch or cancellation before both decisions must not enroll either device. The devices save independently, with the host acknowledging its save before the joiner saves. A lost completion acknowledgment after one save can leave one-sided state; surface that as unconfirmed, not a successful completion or a guaranteed rollback. Compare both device lists before retrying the same pairing. Do not auto-replay an uncertain enrollment.

## Acceptance

- Run old Windows/manual cases unchanged, plus valid and wrong/expired QR proof tests and Android pin/network mismatch tests. Verify no trust is saved merely on scanning or on one approval.
- Open the actual service screens. Exercise the QR path and manual fallback, deny camera permission, cancel/mismatch, and verify the paired-device record survives restart. Keep a C# loopback-host/Android emulator result distinct from an installed Windows host and physical-camera acceptance.
