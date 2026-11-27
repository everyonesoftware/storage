using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Storage.Service.Pairing;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

internal static class QrPairingTests
{
    internal static async Task RunAsync(X509Certificate2 hostIdentity, X509Certificate2 joinIdentity, Guid network,
        Func<CancellationToken, Task<(TcpClient Host, TcpClient Join)>> connect)
    {
        var address = IPAddress.Parse("192.168.100.121");
        var clock = new TestClock();
        var expires = clock.GetUtcNow().AddMinutes(3);
        using var valid = new PairingQrOffer(address, hostIdentity, network, expires, clock);
        using var wrong = new PairingQrOffer(address, hostIdentity, network, expires, clock);
        using var expired = new PairingQrOffer(address, hostIdentity, network, expires, clock);
        using var legacy = new PairingQrOffer(address, hostIdentity, network, expires, clock);
        using var cancelled = new PairingQrOffer(address, hostIdentity, network, expires, clock);
        byte[] proof = Scan(valid);
        byte[] wrongOfferProof = Scan(wrong);
        Check(!proof.SequenceEqual(wrongOfferProof), "Offers must have distinct random secrets");
        await Exchange(valid, proof, Result.Success);
        await FailBeforeHandshake(valid);
        await Exchange(wrong, proof, Result.WrongProof);
        await FailBeforeHandshake(wrong);
        await Exchange(expired, Scan(expired), Result.ExpiredProof);
        await FailBeforeHandshake(expired);
        clock.Now = expires.AddSeconds(-1);
        await Exchange(legacy, [], Result.LegacyAlpn);
        await Exchange(cancelled, Scan(cancelled), Result.Cancelled);
        Console.WriteLine("PASS QR scan roundtrip, interoperable TLS frames, two approvals, wrong/expired proof, replay, ALPN isolation and cancellation");

        byte[] Scan(PairingQrOffer offer)
        {
            BitMatrix modules = offer.Render();
            int size = modules.Width * 3;
            Check(size <= 240 && modules.Width == modules.Height, "QR must fit its native image with at least three pixels per module");
            var pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = modules[x / 3, y / 3] ? (byte)0 : (byte)255;
            var image = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(pixels, size, size, RGBLuminanceSource.BitmapFormat.Gray8)));
            // This fixture is an exactly axis-aligned raster of the encoded modules,
            // not a camera image. Exercise payload decoding without finder heuristics.
            var fields = new QRCodeReader().decode(image, new Dictionary<DecodeHintType, object>
            { [DecodeHintType.PURE_BARCODE] = true })?.Text.Split(':');
            Check(fields is { Length: 6 } && fields[0] == "storage-pair-v1" && fields[1] == address.ToString() &&
                fields[2] == Digest(hostIdentity) && fields[3] == network.ToString("N") &&
                fields[4] == expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) && fields[5].Length == 64 &&
                fields[5].All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F'), "QR payload does not match the Android wire contract");
            return Convert.FromHexString(fields![5]);
        }

        async Task FailBeforeHandshake(PairingQrOffer offer)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (host, join) = await connect(timeout.Token);
            using var h = host;
            using var j = join;
            await ExpectRejected(PairingProtocol.HostQrAsync(host, hostIdentity, "Host PC", offer, UnexpectedPrompt, UnexpectedSave, timeout.Token), timeout.Token);
        }

        async Task Exchange(PairingQrOffer offer, byte[] secret, Result result)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var ct = timeout.Token;
            var (host, join) = await connect(ct);
            using var h = host;
            using var j = join;
            using var tls = new SslStream(join.GetStream());
            var shown = new TaskCompletionSource<PairingChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decide = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PairedDevice? saved = null;
            var hosting = PairingProtocol.HostQrAsync(host, hostIdentity, "Host PC", offer,
                result == Result.Success ? (prompt, token) => { shown.SetResult(prompt); return decide.Task.WaitAsync(token); } : UnexpectedPrompt,
                result == Result.Success ? peer => saved = peer : UnexpectedSave, operation.Token);
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "storage-pairing",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [new SslApplicationProtocol(result == Result.LegacyAlpn ? "storage-pairing/1" : "storage-pairing-qr/1")],
                    ClientCertificates = [joinIdentity],
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => joinIdentity,
                    AllowTlsResume = false,
                    RemoteCertificateValidationCallback = (_, leaf, _, _) => leaf?.GetCertHashString(HashAlgorithmName.SHA256) == Digest(hostIdentity),
                }, ct);
            }
            catch (Exception ex) when (result == Result.LegacyAlpn && ex is AuthenticationException or IOException) { }
            if (result == Result.LegacyAlpn) { await ExpectRejected(hosting, ct); return; }
            Check(tls.IsMutuallyAuthenticated && tls.NegotiatedApplicationProtocol == new SslApplicationProtocol("storage-pairing-qr/1"), "QR must use mutual TLS and its distinct ALPN");
            byte[] hostHello = await Read(tls, 1, ct);
            Check(new Guid(hostHello.AsSpan(0, 16), bigEndian: true) == network, "HostHello network differs from the scanned QR");
            byte[] joinHello = [0, .."Test Android"u8];
            joinHello[0] = (byte)(joinHello.Length - 1);
            await Write(tls, 2, joinHello, ct);
            if (result == Result.Cancelled)
            {
                operation.Cancel();
                try { await hosting.WaitAsync(ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
                throw new InvalidOperationException("Cancelling an outstanding QR proof did not end pairing");
            }
            if (result == Result.ExpiredProof) clock.Now = expires;
            await Write(tls, 11, secret, ct);
            if (result != Result.Success) { await ExpectRejected(hosting, ct); return; }

            var transcript = SHA256.HashData([.."storage-pairing/1 transcript\0"u8,
                ..Convert.FromHexString(Digest(hostIdentity)), ..Convert.FromHexString(Digest(joinIdentity)), ..hostHello, ..joinHello]);
            var hostCommit = await Read(tls, 3, ct);
            var joinNonce = RandomNumberGenerator.GetBytes(32);
            await Write(tls, 4, Commit(2, transcript, joinNonce), ct);
            var hostNonce = await Read(tls, 5, ct);
            Check(hostCommit.SequenceEqual(Commit(1, transcript, hostNonce)), "Host commitment changed in QR mode");
            await Write(tls, 6, joinNonce, ct);
            var challenge = await shown.Task.WaitAsync(ct);
            var hash = SHA256.HashData([.."storage-pairing/1 code\0"u8, ..transcript, ..hostNonce, ..joinNonce]);
            var code = (BinaryPrimitives.ReadUInt64BigEndian(hash) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            Check(challenge == new PairingChallenge("Test Android", code) && saved is null, "The same code must display before enrollment");
            await Write(tls, 7, [], ct);
            Check(saved is null, "Receiving phone approval is not enough to persist trust");
            decide.SetResult(true);
            Check((await Read(tls, 8, ct)).Length == 0, "Host save acknowledgement has an unexpected payload");
            Check(saved == new PairedDevice("Test Android", Digest(joinIdentity), network, false), "Host must save the approved phone before acknowledging");
            Check(!hosting.IsCompleted, "Must wait for the phone save acknowledgement before reporting success");
            await Write(tls, 9, [], ct);
            await hosting.WaitAsync(ct);
        }
    }

    private static async Task ExpectRejected(Task pairing, CancellationToken ct)
    {
        try { await pairing.WaitAsync(ct); }
        catch (AuthenticationException) { return; }
        throw new InvalidOperationException("QR attempt was not rejected before approval");
    }

    private static async Task<byte[]> Read(SslStream tls, byte expected, CancellationToken ct)
    {
        var header = new byte[2];
        await tls.ReadExactlyAsync(header, ct);
        Check(header[0] == expected, "Unexpected QR pairing frame order");
        var bytes = new byte[header[1]];
        await tls.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task Write(SslStream tls, byte type, byte[] bytes, CancellationToken ct) =>
        await tls.WriteAsync((byte[])[type, (byte)bytes.Length, ..bytes], ct);

    private static byte[] Commit(byte role, byte[] transcript, byte[] nonce) =>
        SHA256.HashData([.."storage-pairing/1 commitment\0"u8, role, ..transcript, ..nonce]);

    private static string Digest(X509Certificate cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);
    private static Task<bool> UnexpectedPrompt(PairingChallenge _, CancellationToken ct) => throw new InvalidOperationException("Invalid QR attempt prompted");
    private static void UnexpectedSave(PairedDevice _) => throw new InvalidOperationException("Invalid QR attempt saved trust");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private enum Result { Success, WrongProof, ExpiredProof, LegacyAlpn, Cancelled }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
