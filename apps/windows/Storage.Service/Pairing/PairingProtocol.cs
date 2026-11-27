using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Storage.Service.Pairing;

internal sealed record PairingChallenge(string PeerName, string Code);
internal sealed record PairedDevice(string Name, string Fingerprint, Guid NetworkId, bool IsCoordinator);
internal sealed class PairingUncertainException(string message, Exception cause) : IOException(message, cause);

// The remote certificate is a candidate, not a trusted peer, until the user compares
// the code with the other service and both users approve this exchange.
internal static class PairingProtocol
{
    private static readonly SslApplicationProtocol Protocol = new("storage-pairing/1");
    private static readonly SslApplicationProtocol QrProtocol = new("storage-pairing-qr/1");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int DigestBytes = 32;
    private const int NetworkBytes = 16;
    private const int MaxNameBytes = 128;
    private const SslProtocols Versions = SslProtocols.Tls12 | SslProtocols.Tls13;

    private enum Frame : byte
    {
        HostHello = 1, JoinHello, HostCommit, JoinCommit, HostReveal, JoinReveal,
        JoinApproved, HostSaved, JoinSaved, Rejected, QrProof,
    }

    private sealed class PeerDeclinedException() : OperationCanceledException("Pairing was declined on the other device.");

    internal static Task HostAsync(TcpClient connection, X509Certificate2 identity, string localName,
        Guid networkId, Func<PairingChallenge, CancellationToken, Task<bool>> confirm,
        Action<PairedDevice> save, CancellationToken ct)
    {
        if (networkId == Guid.Empty) throw new ArgumentException("Missing pairing network identity.", nameof(networkId));
        return ExchangeAsync(connection, identity, localName, networkId, host: true, confirm, save, ct, null);
    }

    internal static async Task HostQrAsync(TcpClient connection, X509Certificate2 identity, string localName,
        PairingQrOffer offer, Func<PairingChallenge, CancellationToken, Task<bool>> confirm,
        Action<PairedDevice> save, CancellationToken ct)
    {
        offer.Claim(identity);
        await ExchangeAsync(connection, identity, localName, offer.NetworkId, host: true, confirm, save, ct, null, offer)
            .ConfigureAwait(false);
    }

    internal static Task JoinAsync(TcpClient connection, X509Certificate2 identity, string localName,
        Func<PairingChallenge, CancellationToken, Task<bool>> confirm,
        Action<PairedDevice> save, CancellationToken ct, PairedDevice? expectedCoordinator = null) =>
        ExchangeAsync(connection, identity, localName, Guid.Empty, host: false, confirm, save, ct, expectedCoordinator);

    private static async Task ExchangeAsync(TcpClient connection, X509Certificate2 identity, string localName,
        Guid networkId, bool host, Func<PairingChallenge, CancellationToken, Task<bool>> confirm,
        Action<PairedDevice> save, CancellationToken ct, PairedDevice? expectedCoordinator, PairingQrOffer? offer = null)
    {
        ct.ThrowIfCancellationRequested();
        var name = EncodeName(localName);
        if (!identity.HasPrivateKey) throw new AuthenticationException("Pairing requires a local device private key.");
        var localDigest = identity.GetCertHash(HashAlgorithmName.SHA256);
        using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: true);
        await AuthenticateAsync(tls, identity, host, offer is null ? Protocol : QrProtocol, ct).ConfigureAwait(false);
        using var remoteCertificate = tls.RemoteCertificate ?? throw new AuthenticationException("Missing peer certificate.");
        var peerDigest = remoteCertificate.GetCertHash(HashAlgorithmName.SHA256);
        if (CryptographicOperations.FixedTimeEquals(localDigest, peerDigest))
            throw new AuthenticationException("Cannot pair a device with itself.");

        byte[] hostHello, joinHello;
        string peerName;
        if (host)
        {
            hostHello = new byte[NetworkBytes + 1 + name.Length];
            networkId.TryWriteBytes(hostHello, bigEndian: true, out _);
            hostHello[NetworkBytes] = (byte)name.Length;
            name.CopyTo(hostHello.AsSpan(NetworkBytes + 1));
            await WriteAsync(tls, Frame.HostHello, hostHello, ct).ConfigureAwait(false);
            joinHello = await ReadAsync(tls, Frame.JoinHello, 2, MaxNameBytes + 1, ct).ConfigureAwait(false);
            peerName = DecodeName(joinHello);
            if (offer is not null)
            {
                var proof = await ReadAsync(tls, Frame.QrProof, PairingQrOffer.SecretBytes, PairingQrOffer.SecretBytes, ct)
                    .ConfigureAwait(false);
                try { offer.Verify(proof); }
                finally { CryptographicOperations.ZeroMemory(proof); }
            }
        }
        else
        {
            hostHello = await ReadAsync(tls, Frame.HostHello, NetworkBytes + 2, NetworkBytes + MaxNameBytes + 1, ct)
                .ConfigureAwait(false);
            networkId = new Guid(hostHello.AsSpan(0, NetworkBytes), bigEndian: true);
            if (networkId == Guid.Empty) throw new InvalidDataException("Missing pairing network identity.");
            peerName = DecodeName(hostHello.AsSpan(NetworkBytes));
            joinHello = [(byte)name.Length, ..name];
            await WriteAsync(tls, Frame.JoinHello, joinHello, ct).ConfigureAwait(false);
        }

        var hostDigest = host ? localDigest : peerDigest;
        var joinDigest = host ? peerDigest : localDigest;
        var transcript = SHA256.HashData([.."storage-pairing/1 transcript\0"u8, ..hostDigest, ..joinDigest,
            ..hostHello, ..joinHello]);
        var localNonce = RandomNumberGenerator.GetBytes(DigestBytes);
        var commitment = Commit(host, transcript, localNonce);
        byte[] peerCommitment, peerNonce;
        if (host)
        {
            await WriteAsync(tls, Frame.HostCommit, commitment, ct).ConfigureAwait(false);
            peerCommitment = await ReadDigestAsync(tls, Frame.JoinCommit, ct).ConfigureAwait(false);
            await WriteAsync(tls, Frame.HostReveal, localNonce, ct).ConfigureAwait(false);
            peerNonce = await ReadDigestAsync(tls, Frame.JoinReveal, ct).ConfigureAwait(false);
        }
        else
        {
            peerCommitment = await ReadDigestAsync(tls, Frame.HostCommit, ct).ConfigureAwait(false);
            await WriteAsync(tls, Frame.JoinCommit, commitment, ct).ConfigureAwait(false);
            peerNonce = await ReadDigestAsync(tls, Frame.HostReveal, ct).ConfigureAwait(false);
        }
        if (!CryptographicOperations.FixedTimeEquals(peerCommitment, Commit(!host, transcript, peerNonce)))
            throw new AuthenticationException("The pairing peer did not honor its commitment.");
        if (!host) await WriteAsync(tls, Frame.JoinReveal, localNonce, ct).ConfigureAwait(false);

        var codeHash = SHA256.HashData([.."storage-pairing/1 code\0"u8, ..transcript,
            ..(host ? localNonce : peerNonce), ..(host ? peerNonce : localNonce)]);
        var code = (BinaryPrimitives.ReadUInt64BigEndian(codeHash) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var peer = new PairedDevice(peerName, Convert.ToHexString(peerDigest), networkId, IsCoordinator: !host);
        if (expectedCoordinator is not null && (peer.Fingerprint != expectedCoordinator.Fingerprint || peer.NetworkId != expectedCoordinator.NetworkId))
            throw new IOException("This PC already belongs to a different Storage network.");
        await ApproveAsync(tls, host, new PairingChallenge(peerName, code), peer, confirm, save, ct).ConfigureAwait(false);
    }

    private static async Task AuthenticateAsync(SslStream tls, X509Certificate2 identity, bool host,
        SslApplicationProtocol protocol, CancellationToken ct)
    {
        // TLS proves possession. There is no CA or DNS identity in pairing; only the
        // handshake leaf and the user's out-of-band code comparison can enroll trust.
        static bool Candidate(object _, X509Certificate? cert, X509Chain? _chain, SslPolicyErrors _errors) => cert is not null;
        var chain = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck };
        if (host)
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(identity, null, offline: true),
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = Candidate,
                CertificateChainPolicy = chain,
                EnabledSslProtocols = Versions,
                ApplicationProtocols = [protocol],
                AllowTlsResume = false,
                AllowRenegotiation = false,
            }, ct).ConfigureAwait(false);
        }
        else
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "storage-pairing",
                ClientCertificates = [identity],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
                RemoteCertificateValidationCallback = Candidate,
                CertificateChainPolicy = chain,
                EnabledSslProtocols = Versions,
                ApplicationProtocols = [protocol],
                AllowTlsResume = false,
                AllowRenegotiation = false,
            }, ct).ConfigureAwait(false);
        }
        if (!tls.IsEncrypted || !tls.IsMutuallyAuthenticated || tls.NegotiatedApplicationProtocol != protocol)
            throw new AuthenticationException("Pairing requires encrypted TLS with both device certificates and protocol version 1.");
        if (tls.LocalCertificate is not { } local || !local.GetRawCertData().AsSpan().SequenceEqual(identity.RawData))
            throw new AuthenticationException("TLS did not use the configured device identity.");
    }

    private static async Task ApproveAsync(SslStream tls, bool host, PairingChallenge challenge, PairedDevice peer,
        Func<PairingChallenge, CancellationToken, Task<bool>> confirm, Action<PairedDevice> save, CancellationToken ct)
    {
        using var approval = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = approval.Token;
        var decision = confirm(challenge, token);
        var remote = ReadApprovalAsync(tls, host ? Frame.JoinApproved : Frame.HostSaved, token);
        try
        {
            // Detect a declined/disconnected peer even when this user hasn't answered yet.
            if (await Task.WhenAny(decision, remote).WaitAsync(token).ConfigureAwait(false) == remote)
            {
                await remote.ConfigureAwait(false);
                if (!host) throw new InvalidDataException("Host claimed trust before join approval.");
            }
            if (!await decision.WaitAsync(token).ConfigureAwait(false))
            {
                await WriteAsync(tls, Frame.Rejected, [], token).ConfigureAwait(false);
                throw new OperationCanceledException("Pairing was declined on this device.");
            }

            if (!host)
            {
                if (remote.IsCompleted)
                {
                    await remote.ConfigureAwait(false);
                    throw new InvalidDataException("Host claimed trust before join approval.");
                }
                // Once approval is sent, a lost reply cannot distinguish host save
                // from interruption before save. Don't automatically retry enrollment.
                try
                {
                    await WriteAsync(tls, Frame.JoinApproved, [], token).ConfigureAwait(false);
                    await remote.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or (OperationCanceledException and not PeerDeclinedException))
                {
                    throw new PairingUncertainException("Pairing was interrupted after join approval; check whether the host saved trust.", ex);
                }
            }
            else
            {
                await remote.ConfigureAwait(false);
            }

            try
            {
                token.ThrowIfCancellationRequested();
                save(peer);
            }
            catch (Exception ex) when (!host && ex is (IOException or UnauthorizedAccessException or OperationCanceledException))
            {
                throw new PairingUncertainException("The host saved trust, but this PC could not save its paired device.", ex);
            }
            try
            {
                await WriteAsync(tls, host ? Frame.HostSaved : Frame.JoinSaved, [], token).ConfigureAwait(false);
                if (host) await ReadApprovalAsync(tls, Frame.JoinSaved, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                throw new PairingUncertainException("Local trust was saved, but pairing was interrupted; check the other device.", ex);
            }
        }
        finally
        {
            approval.Cancel();
            _ = ObserveCompletionAsync(decision);
            _ = ObserveCompletionAsync(remote);
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* A concurrent wait can fail after the operation has already ended. */ }
    }

    private static byte[] Commit(bool host, byte[] transcript, byte[] nonce) =>
        SHA256.HashData([.."storage-pairing/1 commitment\0"u8, host ? (byte)1 : (byte)2, ..transcript, ..nonce]);

    private static byte[] EncodeName(string value)
    {
        if (value.Length is 0 or > MaxNameBytes || !IsDisplayName(value) || Utf8.GetByteCount(value) > MaxNameBytes)
            throw new ArgumentException("Invalid pairing display name.", nameof(value));
        return Utf8.GetBytes(value);
    }

    private static bool IsDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.AsSpan().Trim().Length != value.Length) return false;
        foreach (var rune in value.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
        return true;
    }

    private static string DecodeName(ReadOnlySpan<byte> payload)
    {
        if (payload[0] != payload.Length - 1) throw new InvalidDataException("Invalid pairing display name length.");
        try
        {
            var name = Utf8.GetString(payload[1..]);
            if (!IsDisplayName(name)) throw new InvalidDataException("Invalid pairing display name.");
            return name;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid pairing display name encoding.", ex);
        }
    }

    private static async Task WriteAsync(SslStream stream, Frame type, byte[] payload, CancellationToken ct)
    {
        byte[] frame = [(byte)type, checked((byte)payload.Length), ..payload];
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static Task<byte[]> ReadDigestAsync(SslStream stream, Frame expected, CancellationToken ct) =>
        ReadAsync(stream, expected, DigestBytes, DigestBytes, ct);

    private static async Task ReadApprovalAsync(SslStream stream, Frame expected, CancellationToken ct) =>
        _ = await ReadAsync(stream, expected, 0, 0, ct).ConfigureAwait(false);

    private static async Task<byte[]> ReadAsync(SslStream stream, Frame expected, int minimum, int maximum, CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var type = (Frame)header[0];
        var length = header[1];
        if (type == Frame.Rejected && length == 0 && expected is Frame.JoinApproved or Frame.HostSaved)
            throw new PeerDeclinedException();
        if (type != expected || length < minimum || length > maximum)
            throw new InvalidDataException("Unexpected pairing message type or size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}
