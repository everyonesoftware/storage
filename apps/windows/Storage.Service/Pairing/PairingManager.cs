using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using ZXing.Common;

namespace Storage.Service.Pairing;

[SupportedOSPlatform("windows")]
internal sealed class PairingManager(string directory)
{
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(3);
    private readonly PairingState state = new(directory, allowCreate => PairingIdentity.Open(directory, allowCreate));
    private int inProgress;

    internal IReadOnlyList<PairedDevice> List() => state.List()
        .Select(p => new PairedDevice(p.Name, p.Fingerprint, p.NetworkId, p.IsCoordinator)).ToArray();

    internal Task HostAsync(Func<PairingChallenge, CancellationToken, Task<bool>> confirm, CancellationToken ct) => RunAsync(async token =>
    {
        using var identity = state.OpenIdentity(out var known);
        if (known.Any(p => p.IsCoordinator)) throw new IOException("Add devices from your Storage network’s hosting PC.");
        var network = PairingState.HostedNetworkId(identity);
        var local = PairingDiscovery.LocalAddresses();
        if (local.Count == 0) throw new IOException("No local IPv4 address is available. Connect to a local network and try again.");
        using var listener = new TcpListener(IPAddress.Any, PairingDiscovery.TcpPort) { ExclusiveAddressUse = true };
        listener.Start(backlog: 2);
        using var advertise = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task offer = AdvertiseBestEffortAsync(advertise.Token);
        TcpClient? peer = null;
        try
        {
            try { peer = await PairingDiscovery.AcceptLocalAsync(listener, local, PairingLanScope.Current, token).ConfigureAwait(false); }
            finally
            {
                listener.Stop();
                await advertise.CancelAsync().ConfigureAwait(false);
                await offer.ConfigureAwait(false);
            }
            await PairingProtocol.HostAsync(peer, identity, Environment.MachineName, network, confirm,
                p => Save(p, identity), token).ConfigureAwait(false);
        }
        finally { peer?.Dispose(); }
    }, ct);

    internal Task HostQrAsync(IPAddress address, Action<BitMatrix?> display,
        Func<PairingChallenge, CancellationToken, Task<bool>> confirm, CancellationToken ct) => RunAsync(async token =>
    {
        if (!PairingDiscovery.LocalAddresses().Contains(address)) throw new IOException("The selected local address is no longer available.");
        using var identity = state.OpenIdentity(out var known);
        if (known.Any(p => p.IsCoordinator)) throw new IOException("Add devices from your Storage network’s hosting PC.");
        var network = PairingState.HostedNetworkId(identity);
        using var offer = new PairingQrOffer(address, identity, network, DateTimeOffset.UtcNow + OfferLifetime);
        using var listener = new TcpListener(address, PairingDiscovery.TcpPort) { ExclusiveAddressUse = true };
        listener.Start(backlog: 1);
        display(offer.Render());
        // Only this interface and one admitted TCP session; no UDP announcements and
        // no alternate ALPN on the active QR listener.
        using var peer = await PairingDiscovery.AcceptLocalAsync(listener, [address], PairingLanScope.Current, token).ConfigureAwait(false);
        listener.Stop();
        display(null);
        await PairingProtocol.HostQrAsync(peer, identity, Environment.MachineName, offer, confirm,
            p => Save(p, identity), token).ConfigureAwait(false);
    }, ct);

    internal Task JoinAsync(string hostOrAddress, Func<PairingChallenge, CancellationToken, Task<bool>> confirm, CancellationToken ct) => RunAsync(async token =>
    {
        using var identity = state.OpenIdentity(out var known);
        if (known.Any(p => !p.IsCoordinator)) throw new IOException("This PC already hosts a Storage network.");
        using var peer = await PairingDiscovery.ConnectAsync(hostOrAddress, token).ConfigureAwait(false);
        var previous = known.SingleOrDefault();
        var expected = previous is null ? null : new PairedDevice(previous.Name, previous.Fingerprint, previous.NetworkId, true);
        await PairingProtocol.JoinAsync(peer, identity, Environment.MachineName, confirm,
            p => Save(p, identity), token, expected).ConfigureAwait(false);
    }, ct);

    private async Task RunAsync(Func<CancellationToken, Task> run, CancellationToken caller)
    {
        if (Interlocked.CompareExchange(ref inProgress, 1, 0) != 0)
            throw new IOException("The previous pairing attempt is still closing. Try again in a moment.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(OfferLifetime);
            try { await Task.Run(() => run(deadline.Token), deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !caller.IsCancellationRequested)
            { throw new TimeoutException("Pairing expired. Start a new attempt on both devices."); }
        }
        finally { Volatile.Write(ref inProgress, 0); }
    }

    private void Save(PairedDevice p, X509Certificate2 identity) =>
        state.Save(new(p.Name, p.Fingerprint, p.NetworkId, p.IsCoordinator), identity);


    private static async Task AdvertiseBestEffortAsync(CancellationToken ct)
    {
        try { await PairingDiscovery.AdvertiseAsync(Environment.MachineName, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) { /* Manual connections can still work when UDP discovery is unavailable. */ }
    }
}
