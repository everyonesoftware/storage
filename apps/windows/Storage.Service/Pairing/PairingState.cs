using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Storage.Service.Pairing;

internal sealed record TrustRecord(string Name, string Fingerprint, Guid NetworkId, bool IsCoordinator);

internal sealed class PairingState(string directory, Func<bool, X509Certificate2> openIdentity)
{
    private sealed record SavedTrust(string LocalFingerprint, List<TrustRecord> Peers);
    private const int MaxRecords = 64;
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "peers.json");

    internal static Guid HostedNetworkId(X509Certificate2 identity) =>
        new(identity.GetCertHash(HashAlgorithmName.SHA256).AsSpan(0, 16));

    internal IReadOnlyList<TrustRecord> List()
    {
        lock (gate)
        {
            var saved = Load();
            if (saved is null) return [];
            using var identity = openIdentity(false);
            ValidateBinding(saved, identity);
            return saved.Peers;
        }
    }

    internal X509Certificate2 OpenIdentity(out IReadOnlyList<TrustRecord> peers)
    {
        lock (gate)
        {
            var saved = Load();
            var identity = openIdentity(saved is null);
            try
            {
                if (saved is not null) ValidateBinding(saved, identity);
                peers = saved?.Peers ?? [];
                return identity;
            }
            catch { identity.Dispose(); throw; }
        }
    }

    internal void Save(TrustRecord record, X509Certificate2 sessionIdentity)
    {
        lock (gate)
        {
            var saved = Load();
            // Reopen without creating: the identity used in TLS must still be the
            // persisted identity when we commit, even on the first enrollment.
            using var identity = openIdentity(false);
            if (saved is not null) ValidateBinding(saved, identity);
            string fingerprint = identity.GetCertHashString(HashAlgorithmName.SHA256);
            if (fingerprint != sessionIdentity.GetCertHashString(HashAlgorithmName.SHA256))
                throw new IOException("The local Storage device identity changed during pairing.");
            var peers = saved?.Peers ?? [];
            peers.RemoveAll(p => string.Equals(p.Fingerprint, record.Fingerprint, StringComparison.Ordinal));
            peers.Add(record);
            Validate(peers);
            var updated = new SavedTrust(fingerprint, peers);
            ValidateBinding(updated, identity);
            PairingFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(updated), replace: true);
        }
    }

    private SavedTrust? Load()
    {
        try
        {
            var bytes = PairingFiles.Read(path, 128 * 1024);
            var saved = JsonSerializer.Deserialize<SavedTrust>(bytes);
            if (saved?.Peers is null) throw new IOException("Invalid paired device list.");
            Validate(saved.Peers);
            return saved;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw new IOException("Could not read paired devices. Existing state was not changed.", error); }
    }

    private static void ValidateBinding(SavedTrust saved, X509Certificate2 identity)
    {
        if (!identity.HasPrivateKey || saved.LocalFingerprint != identity.GetCertHashString(HashAlgorithmName.SHA256))
            throw new IOException("Paired devices do not match the local Storage device identity.");
        if (!saved.Peers[0].IsCoordinator && saved.Peers[0].NetworkId != HostedNetworkId(identity))
            throw new IOException("Paired devices do not match the local Storage network identity.");
    }

    private static void Validate(List<TrustRecord> peers)
    {
        if (peers.Count is < 1 or > MaxRecords || peers.Any(p => p is null || p.Name is null || p.Name.Length is < 1 or > 128 ||
                p.Name.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
                p.NetworkId == Guid.Empty || p.Fingerprint is not { Length: 64 } || p.Fingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) ||
            peers.Select(p => p.Fingerprint).Distinct(StringComparer.Ordinal).Count() != peers.Count ||
            peers.Select(p => p.NetworkId).Distinct().Count() > 1 || (peers.Count > 1 && peers.Any(p => p.IsCoordinator)))
            throw new IOException("Invalid paired device state or a different Storage network is already configured.");
    }
}

internal static class PairingFiles
{
    internal static byte[] Read(string path, int limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length <= 0 || file.Length > limit) throw new IOException("Invalid pairing state file size.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("Pairing state changed during read.");
        return bytes;
    }

    internal static void Write(string path, ReadOnlySpan<byte> bytes, bool replace)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: replace);
        }
        finally { File.Delete(temp); }
    }
}
