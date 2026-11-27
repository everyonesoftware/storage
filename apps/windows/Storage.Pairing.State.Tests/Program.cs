using Storage.Service.Pairing;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

string root = Path.Combine(Path.GetTempPath(), "storage-pairing-state-" + Guid.NewGuid().ToString("N"));
try
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=State fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var now = DateTimeOffset.UtcNow;
    using var identity = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    PairingState Reopen() => new(root, _ => new X509Certificate2(identity));
    var state = Reopen();
    var network = new Guid(identity.GetCertHash(HashAlgorithmName.SHA256).AsSpan(0, 16));
    var first = new TrustRecord("Studio PC", new string('A', 64), network, false);
    var renamed = first with { Name = "Desk PC" };
    var second = new TrustRecord("Laptop", new string('B', 64), network, false);
    Require(state.List().Count == 0, "a new profile must have no paired peers");
    state.Save(first, identity);
    Require(Reopen().List().SequenceEqual([first]), "an approved peer must survive a state reload");
    state.Save(renamed, identity);
    Require(Reopen().List().SequenceEqual([renamed]), "retry replaces the same identity rather than duplicating it");
    state.Save(second, identity);
    Require(Reopen().List().ToHashSet().SetEquals([renamed, second]), "saving a second peer must preserve the first peer");
    Reject(() => state.Save(second with { NetworkId = Guid.NewGuid() }, identity));
    Reject(() => state.Save(second with { IsCoordinator = true }, identity));
    Reject(() => state.Save(second with { Fingerprint = "invalid" }, identity));
    Require(Reopen().List().ToHashSet().SetEquals([renamed, second]), "invalid updates must not change trusted records");
    File.WriteAllText(Path.Combine(root, "peers.json"), "not a state file");
    Reject(() => Reopen().List());
    Reject(() => Reopen().Save(first, identity));
    Require(File.ReadAllText(Path.Combine(root, "peers.json")) == "not a state file", "must not replace broken state with new trust");
    Console.WriteLine("Pairing state: empty, persisted reload, idempotent retry, peer preservation, invalid update and corruption checks passed.");
    TestIdentityBinding(Path.Combine(root, "bound"));
    if (OperatingSystem.IsWindows())
    {
        string identityRoot = Path.Combine(root, "identity-profile");
        Reject(() => { using var _ = PairingIdentity.Open(identityRoot, allowCreate: false); });
        byte[] firstCertificate;
        using (var windowsIdentity = PairingIdentity.Open(identityRoot, allowCreate: true))
        {
            firstCertificate = windowsIdentity.RawData;
            Require(windowsIdentity.HasPrivateKey, "new private identity unavailable");
        }
        using (var windowsIdentity = PairingIdentity.Open(identityRoot, allowCreate: false))
        using (var windowsKey = windowsIdentity.GetRSAPrivateKey())
        {
            Require(windowsIdentity.RawData.SequenceEqual(firstCertificate), "identity changed after reopen");
            Require(windowsKey?.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Length > 0, "persisted private key unavailable");
        }
        string protectedPath = Path.Combine(identityRoot, "identity.dpapi");
        byte[] broken = [7, 3, 1];
        File.WriteAllBytes(protectedPath, broken);
        Reject(() => { using var _ = PairingIdentity.Open(identityRoot, allowCreate: true); });
        Require(File.ReadAllBytes(protectedPath).SequenceEqual(broken), "unreadable identity must not be silently replaced");
        Console.WriteLine("Windows protected identity: create, reopen, sign and reject corrupted state passed.");
    }
    else Console.WriteLine("Windows protected identity runtime checks require Windows.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

static void TestIdentityBinding(string directory)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=State fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var now = DateTimeOffset.UtcNow;
    using var original = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    using var replacement = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    X509Certificate2? persisted = original;
    var state = new PairingState(directory, allowCreate =>
    {
        Require(!allowCreate, "must never create a replacement while trust exists");
        return persisted is null ? throw new FileNotFoundException("Missing test identity") : new X509Certificate2(persisted);
    });
    var peer = new TrustRecord("Coordinator", new string('C', 64), Guid.NewGuid(), true);
    state.Save(peer, original);
    Require(state.List().SequenceEqual([peer]), "joiner trust did not reopen with its original identity");
    using (var reopened = state.OpenIdentity(out var peers))
        Require(reopened.RawData.SequenceEqual(original.RawData) && peers.SequenceEqual([peer]), "pairing did not reopen the bound identity");
    string path = Path.Combine(directory, "peers.json");
    byte[] before = File.ReadAllBytes(path);
    foreach (var candidate in new X509Certificate2?[] { replacement, null })
    {
        persisted = candidate;
        Reject(() => state.List());
        Reject(() => { using var unavailable = state.OpenIdentity(out _); });
        Reject(() => state.Save(peer with { Name = "No overwrite" }, original));
        Require(File.ReadAllBytes(path).SequenceEqual(before), "identity failure must leave the trust file unchanged");
    }
    persisted = original;
    Reject(() => state.Save(peer with { Name = "Wrong session" }, replacement));
    Require(File.ReadAllBytes(path).SequenceEqual(before), "must not save from a different session identity");

    var host = new PairingState(Path.Combine(directory, "host"), _ => new X509Certificate2(original));
    var network = new Guid(original.GetCertHash(HashAlgorithmName.SHA256).AsSpan(0, 16));
    var member = peer with { Name = "Member", NetworkId = network, IsCoordinator = false };
    Reject(() => host.Save(member with { NetworkId = Guid.NewGuid() }, original));
    host.Save(member, original);
    Require(host.List().SequenceEqual([member]), "host trust did not reopen on the certificate-derived network");
    string hostPath = Path.Combine(directory, "host", "peers.json");
    string invalidNetwork = File.ReadAllText(hostPath).Replace(network.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
    File.WriteAllText(hostPath, invalidNetwork);
    Reject(() => host.List());
    Reject(() => host.Save(member, original));
    Require(File.ReadAllText(hostPath) == invalidNetwork, "invalid hosted network must not be overwritten");

    string unbound = System.Text.Json.JsonSerializer.Serialize(new[] { peer });
    File.WriteAllText(path, unbound);
    Reject(() => state.List());
    Reject(() => state.Save(peer, original));
    Require(File.ReadAllText(path) == unbound, "unbound trust must not be adopted or overwritten");
    Console.WriteLine("Pairing state: identity-bound reload, missing/replaced identity, hosted network binding and unbound trust rejection passed.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action operation)
{
    try { operation(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("Unusable pairing state must fail closed.");
}
