using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Storage.Service.Pairing;

using var hostIdentity = CreateIdentity();
using var joinIdentity = CreateIdentity();
var network = Guid.NewGuid();
await ReciprocalTrust();
Console.WriteLine("PASS reciprocal trust after matching codes and both approvals");
await ProtocolFailures.RunAsync(hostIdentity, joinIdentity, network, Connect);
await QrPairingTests.RunAsync(hostIdentity, joinIdentity, network, Connect);

async Task ReciprocalTrust()
{
    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var (host, join) = await Connect(ct.Token);
    using (host)
    using (join)
    {
        PairedDevice? hostSave = null;
        PairedDevice? joinSave = null;
        var hostPrompt = new TaskCompletionSource<PairingChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinPrompt = new TaskCompletionSource<PairingChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hosting = PairingProtocol.HostAsync(host, hostIdentity, "Host machine", network,
            (challenge, token) => { hostPrompt.SetResult(challenge); return hostDecision.Task.WaitAsync(token); },
            peer => hostSave = peer, ct.Token);
        var joining = PairingProtocol.JoinAsync(join, joinIdentity, "Join machine",
            (challenge, token) => { joinPrompt.SetResult(challenge); return joinDecision.Task.WaitAsync(token); },
            peer => joinSave = peer, ct.Token);
        var running = Task.WhenAll(hosting, joining);
        var prompts = Task.WhenAll(hostPrompt.Task, joinPrompt.Task);
        Check(await Task.WhenAny(running, prompts).WaitAsync(ct.Token) == prompts,
            "Both prompts must appear before either approval");
        var shown = await prompts;
        Check(shown[0].PeerName == "Join machine" && shown[1].PeerName == "Host machine", "Peer names must match");
        Check(shown[0].Code == shown[1].Code && shown[0].Code.Length == 6 && shown[0].Code.All(char.IsAsciiDigit),
            "Both displays must show the same six digits");
        Check(hostSave is null && joinSave is null, "No trust before user decisions");
        joinDecision.SetResult(true);
        hostDecision.SetResult(true);
        await running.WaitAsync(ct.Token);
        Check(hostSave == new PairedDevice("Join machine", Fingerprint(joinIdentity), network, false), "Host stored wrong peer");
        Check(joinSave == new PairedDevice("Host machine", Fingerprint(hostIdentity), network, true), "Joiner stored wrong coordinator");
    }
}

static async Task<(TcpClient, TcpClient)> Connect(CancellationToken ct)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var joining = new TcpClient(AddressFamily.InterNetwork);
    try
    {
        await joining.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        return (await listener.AcceptTcpClientAsync(ct), joining);
    }
    catch
    {
        joining.Dispose();
        throw;
    }
}

static X509Certificate2 CreateIdentity()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=Storage device", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    var pfx = certificate.Export(X509ContentType.Pkcs12);
    try { return X509CertificateLoader.LoadPkcs12(pfx, (string?)null, X509KeyStorageFlags.UserKeySet); }
    finally { CryptographicOperations.ZeroMemory(pfx); }
}

static string Fingerprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);

static void Check(bool success, string message)
{
    if (!success) throw new InvalidOperationException(message);
}
