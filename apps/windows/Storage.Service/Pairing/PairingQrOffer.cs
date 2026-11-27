using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace Storage.Service.Pairing;

// Service-private and in memory only. Never expose the payload as display text.
internal sealed class PairingQrOffer(IPAddress address, X509Certificate2 identity, Guid networkId,
    DateTimeOffset expires, TimeProvider? clock = null) : IDisposable
{
    internal const int SecretBytes = 32;
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(SecretBytes);
    private readonly string fingerprint = identity.GetCertHashString(HashAlgorithmName.SHA256);
    private readonly long expiresAt = expires.ToUnixTimeSeconds();
    private int used;
    internal Guid NetworkId { get; } = networkId;

    private bool Expired => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() >= expiresAt;

    internal BitMatrix Render()
    {
        if (Volatile.Read(ref used) != 0 || Expired) throw new AuthenticationException("The QR offer is no longer available.");
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"storage-pair-v1:{address}:{fingerprint}:{NetworkId:N}:{expiresAt}:{Convert.ToHexString(secret)}");
        // Return a module matrix including the four-module quiet zone. The native window
        // scales it by an integer number of physical pixels without filtering.
        return new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 4, [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M });
    }

    internal void Claim(X509Certificate2 connectionIdentity)
    {
        if (Interlocked.Exchange(ref used, 1) != 0 || Expired ||
            connectionIdentity.GetCertHashString(HashAlgorithmName.SHA256) != fingerprint)
            throw new AuthenticationException("The QR offer is no longer available.");
    }

    internal void Verify(ReadOnlySpan<byte> proof)
    {
        bool matches = CryptographicOperations.FixedTimeEquals(secret, proof);
        if (Expired || !matches) throw new AuthenticationException("Invalid or expired QR pairing proof.");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref used, 1);
        CryptographicOperations.ZeroMemory(secret);
    }
}
