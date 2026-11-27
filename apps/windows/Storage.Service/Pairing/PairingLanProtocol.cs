using System.Globalization;
using System.Text;

namespace Storage.Service.Pairing;

// A hint protocol, not authentication. Equal-sized requests and replies avoid
// byte amplification. Do not add identity material or pairing secrets here.
internal static class PairingLanProtocol
{
    private const int NameOffset = 23;
    private const int MaxNameBytes = 64;
    internal const int PacketSize = NameOffset + MaxNameBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Query(Guid nonce) => Packet(1, nonce);

    internal static byte[] Reply(Guid nonce, string name)
    {
        var packet = Packet(2, nonce);
        packet[22] = (byte)StrictUtf8.GetBytes(name.AsSpan(), packet.AsSpan(NameOffset));
        return packet;
    }

    internal static bool TryReadQuery(ReadOnlySpan<byte> packet, out Guid nonce)
    {
        nonce = default;
        if (!HasHeader(packet, 1) || packet[22..].ContainsAnyExcept((byte)0)) return false;
        nonce = new Guid(packet.Slice(6, 16), bigEndian: true);
        return nonce != Guid.Empty;
    }

    internal static bool TryReadReply(ReadOnlySpan<byte> packet, Guid nonce, out string name)
    {
        name = "";
        if (!HasHeader(packet, 2) || new Guid(packet.Slice(6, 16), bigEndian: true) != nonce) return false;
        int length = packet[22];
        if (length is < 1 or > MaxNameBytes || packet[(NameOffset + length)..].ContainsAnyExcept((byte)0)) return false;
        try
        {
            var decoded = StrictUtf8.GetString(packet.Slice(NameOffset, length));
            if (DisplayName(decoded) != decoded) return false;
            name = decoded;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    internal static string DisplayName(string name)
    {
        var text = new StringBuilder();
        int bytes = 0;
        bool addSpace = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                addSpace = bytes != 0;
                continue;
            }
            int length = rune.Utf8SequenceLength + (addSpace ? 1 : 0);
            if (bytes + length > MaxNameBytes) break;
            if (addSpace) text.Append(' ');
            text.Append(rune.ToString());
            bytes += length;
            addSpace = false;
        }
        return bytes == 0 ? "Storage device" : text.ToString();
    }

    private static byte[] Packet(byte kind, Guid nonce)
    {
        var bytes = new byte[PacketSize];
        "STPD"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[5] = kind;
        nonce.TryWriteBytes(bytes.AsSpan(6, 16), bigEndian: true, out _);
        return bytes;
    }

    private static bool HasHeader(ReadOnlySpan<byte> packet, byte kind) =>
        packet.Length == PacketSize && packet[..4].SequenceEqual("STPD"u8) && packet[4] == 1 && packet[5] == kind;
}
