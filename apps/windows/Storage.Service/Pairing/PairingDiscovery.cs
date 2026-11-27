using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Storage.Service.Pairing;

// Display-only discovery result. The address and name do not establish identity.
internal sealed record NearbyDevice(string Name, IPAddress Address);

internal static class PairingDiscovery
{
    internal const int TcpPort = 48553;
    internal const int UdpPort = 48554;
    private const int MaxProbes = 16;
    private const int MaxResults = 32;
    private const int MaxConnectAttempts = 8;
    private static readonly TimeSpan ScanWindow = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(900);

    internal static IReadOnlyList<IPAddress> LocalAddresses() =>
        PairingLanScope.Current().Select(scope => scope.Address).Distinct().ToArray();

    internal static bool IsLocalPeer(IPAddress address) => FindScope(address, PairingLanScope.Current()) is not null;

    private static bool IsLocalPeerOnAddress(IPAddress peer, IPAddress local, IReadOnlyList<PairingLanScope> scopes) =>
        !scopes.Any(scope => scope.Address.Equals(peer)) &&
        scopes.Any(scope => scope.Address.Equals(local) && scope.ContainsHost(peer));

    internal static async Task<TcpClient> AcceptLocalAsync(TcpListener listener, IReadOnlyList<IPAddress> local,
        Func<IReadOnlyList<PairingLanScope>> scopes, CancellationToken ct)
    {
        while (true)
        {
            var connection = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            bool admitted = false;
            try
            {
                admitted = connection.Client.LocalEndPoint is IPEndPoint target && local.Contains(target.Address) &&
                    connection.Client.RemoteEndPoint is IPEndPoint source &&
                    IsLocalPeerOnAddress(source.Address, target.Address, scopes());
                if (admitted) return connection;
            }
            finally { if (!admitted) connection.Dispose(); }
        }
    }

    internal static async Task AdvertiseAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scopes = PairingLanScope.Current();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
        await AdvertiseAsync(name, scopes, socket, ct).ConfigureAwait(false);
    }

    internal static async Task AdvertiseAsync(string name, IReadOnlyList<PairingLanScope> scopes, Socket socket,
        CancellationToken ct)
    {
        string label = PairingLanProtocol.DisplayName(name);
        var packet = new byte[PairingLanProtocol.PacketSize + 1];
        var localAddresses = scopes.Select(s => s.Address).ToHashSet();
        var replies = new Dictionary<PairingLanScope, Socket>();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var received = await ReceiveAsync(socket, packet, ct).ConfigureAwait(false);
                if (received is not { } query || query.RemoteEndPoint is not IPEndPoint peer || peer.Port == 0 ||
                    localAddresses.Contains(peer.Address) ||
                    !PairingLanProtocol.TryReadQuery(packet.AsSpan(0, query.ReceivedBytes), out Guid nonce)) continue;

                // Check the ingress adapter, destination and source together. A matching
                // private subnet on a different interface does not qualify this packet.
                foreach (var scope in scopes)
                {
                    if (query.PacketInformation.Interface != scope.InterfaceIndex || !scope.ContainsHost(peer.Address) ||
                        !(scope.Address.Equals(query.PacketInformation.Address) ||
                          Equals(scope.Broadcast, query.PacketInformation.Address))) continue;
                    try
                    {
                        if (!replies.TryGetValue(scope, out var replySocket))
                        {
                            replySocket = OpenUdp(scope, broadcast: false);
                            replies.Add(scope, replySocket);
                        }
                        await replySocket.SendToAsync(PairingLanProtocol.Reply(nonce, label), SocketFlags.None, peer, ct)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException) when (!ct.IsCancellationRequested) { /* Adapter or route went away. */ }
                    break; // At most one unicast reply per solicitation.
                }
            }
        }
        finally
        {
            foreach (var reply in replies.Values) reply.Dispose();
        }
    }

    internal static Task<IReadOnlyList<NearbyDevice>> FindAsync(CancellationToken ct) =>
        FindAsync(PairingLanScope.Current(), static scope => scope.Broadcast, UdpPort, ScanWindow, ct);

    internal static async Task<IReadOnlyList<NearbyDevice>> FindAsync(IReadOnlyList<PairingLanScope> scopes,
        Func<PairingLanScope, IPAddress?> destination, int port, TimeSpan window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(window);
        var localAddresses = scopes.Select(s => s.Address).ToHashSet();
        var devices = new Dictionary<IPAddress, NearbyDevice>();
        Guid nonce = Guid.NewGuid();
        var solicitation = PairingLanProtocol.Query(nonce);
        var targets = scopes.Select(scope => (Scope: scope, Target: destination(scope)))
            .Where(probe => probe.Target is not null)
            .Distinct()
            .Take(MaxProbes);
        await Task.WhenAll(targets.Select(probe => ScanAsync(probe.Scope, probe.Target!))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return devices.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Address.ToString(), StringComparer.Ordinal).ToArray();

        async Task ScanAsync(PairingLanScope scope, IPAddress target)
        {
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var socket = OpenUdp(scope, broadcast: true);
                await socket.SendToAsync(solicitation, SocketFlags.None, new IPEndPoint(target, port), deadline.Token)
                    .ConfigureAwait(false);
                var packet = new byte[PairingLanProtocol.PacketSize + 1];
                while (!deadline.IsCancellationRequested)
                {
                    var received = await ReceiveAsync(socket, packet, deadline.Token).ConfigureAwait(false);
                    if (received is not { } reply || reply.RemoteEndPoint is not IPEndPoint sender ||
                        reply.PacketInformation.Interface != scope.InterfaceIndex ||
                        !scope.Address.Equals(reply.PacketInformation.Address) || localAddresses.Contains(sender.Address) ||
                        !scope.ContainsHost(sender.Address) ||
                        !PairingLanProtocol.TryReadReply(packet.AsSpan(0, reply.ReceivedBytes), nonce, out string name)) continue;
                    lock (devices)
                    {
                        if (devices.Count < MaxResults) devices.TryAdd(sender.Address, new NearbyDevice(name, sender.Address));
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) { /* Another adapter or manual entry may still work. */ }
        }
    }

    internal static Task<TcpClient> ConnectAsync(string hostOrAddress, CancellationToken ct) =>
        ConnectAsync(hostOrAddress, PairingLanScope.Current,
            static (host, token) => Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token),
            TcpPort, ConnectTimeout, ct);

    internal static async Task<TcpClient> ConnectAsync(string hostOrAddress, Func<IReadOnlyList<PairingLanScope>> scopes,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, int port, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (host, literal) = ParseManualAddress(hostOrAddress);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var candidates = literal is not null ? [literal] :
                await resolve(host, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var local = scopes();
            var eligible = candidates.Distinct().Select(address => (Address: address, Scope: FindScope(address, local)))
                .Where(pair => pair.Scope.HasValue).Take(MaxConnectAttempts).ToArray();
            deadline.Token.ThrowIfCancellationRequested();
            if (eligible.Length == 0)
                throw new ArgumentException("Enter an IPv4 address or host on the same local network.", nameof(hostOrAddress));

            Exception? lastFailure = null;
            foreach (var (address, scope) in eligible)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(AttemptTimeout);
                var client = new TcpClient(AddressFamily.InterNetwork);
                bool handedOff = false;
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    BindOutgoing(client.Client, scope!.Value);
                    await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    handedOff = true;
                    return client;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested && attempt.IsCancellationRequested)
                {
                    lastFailure = new TimeoutException("The local pairing endpoint did not respond.");
                }
                catch (SocketException error) when (!deadline.IsCancellationRequested) { lastFailure = error; }
                finally { if (!handedOff) client.Dispose(); }
            }
            deadline.Token.ThrowIfCancellationRequested();
            throw lastFailure!; // At least one eligible attempt failed.
        }
        catch (Exception error) when (deadline.IsCancellationRequested && error is OperationCanceledException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The local pairing connection timed out.", error);
        }
    }

    private static (string Host, IPAddress? Literal) ParseManualAddress(string hostOrAddress)
    {
        ArgumentNullException.ThrowIfNull(hostOrAddress);
        var host = hostOrAddress.Trim();
        if (host.Length is >= 1 and <= 253)
        {
            if (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
                host == address.ToString()) return (host, address);
            var labels = (host.EndsWith('.') ? host[..^1] : host).Split('.');
            if (address is null && !host.All(c => char.IsAsciiDigit(c) || c == '.') &&
                labels.All(label => label.Length is >= 1 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) &&
                    char.IsAsciiLetterOrDigit(label[^1]) && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return (host, null);
        }
        throw new ArgumentException("Enter a dotted IPv4 address or hostname without a port.", nameof(hostOrAddress));
    }

    private static PairingLanScope? FindScope(IPAddress address, IReadOnlyList<PairingLanScope> scopes)
    {
        PairingLanScope? best = null;
        foreach (var scope in scopes)
        {
            if (scope.Address.Equals(address)) return null;
            if (scope.ContainsHost(address) && (best is null || scope.PrefixLength > best.Value.PrefixLength)) best = scope;
        }
        return best;
    }

    private static Socket OpenUdp(PairingLanScope scope, bool broadcast)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (broadcast)
            {
                socket.EnableBroadcast = true;
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            }
            BindOutgoing(socket, scope);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static void BindOutgoing(Socket socket, PairingLanScope scope)
    {
        socket.Ttl = 1; // Do not send discovery or pairing traffic through a router.
        if (OperatingSystem.IsWindows())
        {
            // Winsock IP_UNICAST_IF uses an interface index in network byte order.
            const int IpUnicastIf = 31;
            Span<byte> option = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(option, scope.InterfaceIndex);
            socket.SetRawSocketOption((int)SocketOptionLevel.IP, IpUnicastIf, option);
        }
        socket.Bind(new IPEndPoint(scope.Address, 0));
    }

    private static async ValueTask<SocketReceiveMessageFromResult?> ReceiveAsync(Socket socket, byte[] packet,
        CancellationToken ct)
    {
        try
        {
            var received = await socket.ReceiveMessageFromAsync(packet, SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            return (received.SocketFlags & SocketFlags.Truncated) == 0 ? received : null;
        }
        catch (SocketException error) when (!ct.IsCancellationRequested &&
            error.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
        {
            // Winsock can report oversized datagrams and asynchronous ICMP errors this way.
            return null;
        }
    }
}
