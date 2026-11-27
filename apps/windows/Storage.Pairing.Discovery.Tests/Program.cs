using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Storage.Service.Pairing;

(string Name, Func<Task> Run)[] tests =
[
    ("private IPv4 scope and broadcast boundaries", TestScope),
    ("bounded versioned discovery packets", TestPackets),
    ("real UDP solicitation and source-address response", TestUdp),
    ("UDP scan ignores foreign rounds and malformed packets and deduplicates replies", TestIgnoredReplies),
    ("manual resolution filters candidates and opens an owned TCP client", TestTcp),
    ("TCP admission requires the exact local destination subnet", TestTcpAdmission),
    ("manual IPv4 literal skips DNS", TestTcpLiteral),
    ("manual address validation and deadline", TestManualRejections),
];
foreach (var (name, run) in tests)
{
    await run().WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine($"PASS {name}");
}

static IPAddress Ip(string text) => IPAddress.Parse(text);
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static Task TestScope()
{
    Check(PairingLanScope.TryCreate(Ip("192.168.12.7"), 24, 3, out var scope), "expected private scope");
    Check(scope.Broadcast!.Equals(Ip("192.168.12.255")), "incorrect directed broadcast");
    Check(scope.ContainsHost(Ip("192.168.12.1")), "same subnet peer rejected");
    foreach (var host in new[] { "192.168.12.0", "192.168.12.255", "192.168.13.1", "::ffff:192.168.12.1", "8.8.8.8" })
        Check(!scope.ContainsHost(Ip(host)), $"ineligible peer accepted: {host}");
    Check(PairingLanScope.TryCreate(Ip("169.254.8.7"), 16, 3, out _), "link-local scope rejected");
    Check(PairingLanScope.TryCreate(Ip("10.7.9.0"), 31, 3, out var pointToPoint)
        && pointToPoint.Broadcast == null && pointToPoint.ContainsHost(Ip("10.7.9.1")), "incorrect /31 behavior");
    Check(!PairingLanScope.TryCreate(Ip("192.168.12.7"), 8, 3, out _), "subnet extends beyond local scope");
    foreach (var host in new[] { "127.0.0.1", "8.8.8.8", "100.64.0.1", "224.0.0.1", "0.0.0.0", "::1" })
        Check(!PairingLanScope.TryCreate(Ip(host), 24, 3, out _), $"ineligible interface accepted: {host}");
    return Task.CompletedTask;
}

static async Task TestTcpAdmission()
{
    var target = Ip("127.0.0.1");
    var other = Ip("127.0.1.1");
    PairingLanScope[] scopes = [new(target, 24, 1), new(other, 24, 2)];
    using var listener = new TcpListener(IPAddress.Any, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    using var crossSubnet = new TcpClient(new IPEndPoint(Ip("127.0.1.2"), 0));
    await crossSubnet.ConnectAsync(target, port, deadline.Token);
    using var sameSubnet = new TcpClient(new IPEndPoint(Ip("127.0.0.2"), 0));
    await sameSubnet.ConnectAsync(target, port, deadline.Token);
    using var accepted = await PairingDiscovery.AcceptLocalAsync(listener, [target, other], () => scopes, deadline.Token);
    Check(Equals(accepted.Client.RemoteEndPoint, sameSubnet.Client.LocalEndPoint),
        "TCP listener admitted a peer from a different local destination subnet");
}

static Task TestPackets()
{
    var nonce = Guid.NewGuid();
    var query = PairingLanProtocol.Query(nonce);
    Check(PairingLanProtocol.TryReadQuery(query, out var actual) && nonce == actual, "query did not round-trip");
    var name = PairingLanProtocol.DisplayName(" \0 Desk\n\u202e PC  ");
    Check(name == "Desk PC", "unsafe name was not normalized");
    Check(PairingLanProtocol.DisplayName("\t\0") == "Storage device", "missing fallback label");
    var longName = PairingLanProtocol.DisplayName(string.Concat(Enumerable.Repeat("🗄", 40)));
    Check(Encoding.UTF8.GetByteCount(longName) == 64, "name limit split a scalar or used the wrong units");
    var reply = PairingLanProtocol.Reply(nonce, name);
    Check(reply.Length == query.Length, "response is larger than solicitation");
    Check(PairingLanProtocol.TryReadReply(reply, nonce, out var read) && name == read, "reply did not round-trip");
    Check(!PairingLanProtocol.TryReadReply(reply, Guid.NewGuid(), out _), "wrong round accepted");
    Check(!PairingLanProtocol.TryReadQuery(reply, out _), "reply accepted as a query");
    Check(!PairingLanProtocol.TryReadReply(reply[..^1], nonce, out _), "short packet accepted");
    Check(!PairingLanProtocol.TryReadReply([.. reply, 0], nonce, out _), "long packet accepted");
    foreach (var (offset, value) in new (int, byte)[] { (0, 0), (4, 2), (22, 65), (23, 0xff), (23, 1), (86, 1) })
    {
        var malformed = reply.ToArray();
        malformed[offset] = value;
        Check(!PairingLanProtocol.TryReadReply(malformed, nonce, out _), $"accepted malformed byte at {offset}");
    }
    return Task.CompletedTask;
}

static PairingLanScope LoopbackScope(string address) => new(Ip(address), 8,
    NetworkInterface.GetAllNetworkInterfaces().Single(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        .GetIPProperties().GetIPv4Properties().Index);

static async Task TestUdp()
{
    var finder = LoopbackScope("127.0.0.1");
    var responder = LoopbackScope("127.0.0.2");
    using var inbound = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    inbound.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
    inbound.Bind(new IPEndPoint(IPAddress.Any, 0));
    int port = ((IPEndPoint)inbound.LocalEndPoint!).Port;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var advertising = PairingDiscovery.AdvertiseAsync("  Nearby\n PC ", [responder], inbound, stop.Token);
    try
    {
        // Linux additionally exercises real directed broadcast. Use unicast to the
        // same responder on Windows, where loopback is not a broadcast test LAN.
        var target = OperatingSystem.IsLinux() ? finder.Broadcast! : responder.Address;
        var devices = await PairingDiscovery.FindAsync([finder], _ => target, port, TimeSpan.FromMilliseconds(550), stop.Token);
        Check(devices.Count == 1 && devices[0] == new NearbyDevice("Nearby PC", responder.Address),
            "discovery did not return the UDP sender address and display name");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => PairingDiscovery.FindAsync([finder], _ => target, port,
            TimeSpan.FromSeconds(1), cancelled.Token));
    }
    finally
    {
        await stop.CancelAsync();
        await Throws<OperationCanceledException>(() => advertising);
    }
}

static async Task TestIgnoredReplies()
{
    var finder = LoopbackScope("127.0.0.1");
    var responder = LoopbackScope("127.0.0.2");
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(responder.Address, 0));
    int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var finding = PairingDiscovery.FindAsync([finder], _ => responder.Address, port, TimeSpan.FromSeconds(1), stop.Token);
    var request = new byte[PairingLanProtocol.PacketSize];
    var sender = await socket.ReceiveFromAsync(request, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
    Check(PairingLanProtocol.TryReadQuery(request.AsSpan(0, sender.ReceivedBytes), out var nonce), "missing round nonce");
    var valid = PairingLanProtocol.Reply(nonce, "Real PC");
    byte[][] packets = [PairingLanProtocol.Reply(Guid.Empty, "Wrong round"), [.. valid, 0], valid, valid];
    foreach (var packet in packets)
        await socket.SendToAsync(packet, SocketFlags.None, sender.RemoteEndPoint, stop.Token);
    var devices = await finding;
    Check(devices.Count == 1 && devices[0] == new NearbyDevice("Real PC", responder.Address),
        "a malformed or foreign reply hid the valid hint, or a duplicate escaped");
}

static async Task TestTcp()
{
    var source = LoopbackScope("127.0.0.2");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    int resolutions = 0;
    var accept = listener.AcceptTcpClientAsync(stop.Token);
    using var client = await PairingDiscovery.ConnectAsync(" pair-box.local ", () => [source], (name, _) =>
    {
        Check(name == "pair-box.local", "manual hostname changed before resolution");
        resolutions++;
        return Task.FromResult(new[] { Ip("::1"), Ip("8.8.8.8"), source.Address, Ip("127.0.0.3"), IPAddress.Loopback });
    }, port, TimeSpan.FromSeconds(2), stop.Token);
    using var peer = await accept;
    Check(resolutions == 1 && ((IPEndPoint)peer.Client.RemoteEndPoint!).Address.Equals(source.Address),
        "connector did not filter candidates and bind the source address");
    await client.GetStream().WriteAsync(new byte[] { 42 }, stop.Token);
    var data = new byte[1];
    await peer.GetStream().ReadExactlyAsync(data, stop.Token);
    Check(data[0] == 42, "returned client cannot carry data");
}

static async Task TestTcpLiteral()
{
    var source = LoopbackScope("127.0.0.2");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    using var client = await PairingDiscovery.ConnectAsync("127.0.0.1", () => [source],
        (_, _) => throw new Exception("A validated IPv4 literal should not be resolved"),
        port, TimeSpan.FromSeconds(2), stop.Token);
    using var remote = await listener.AcceptTcpClientAsync(stop.Token);
    Check(((IPEndPoint)remote.Client.RemoteEndPoint!).Address.Equals(source.Address), "literal used the wrong source");
}

static async Task TestManualRejections()
{
    foreach (var invalid in new[] { "https://pair-box", "10.0.0.2:48553", "127.1", "010.0.0.2", "10.0.0.999", "::1", "foo/bar", "host name", "host..local" })
        await Throws<ArgumentException>(() => PairingDiscovery.ConnectAsync(invalid, CancellationToken.None));
    foreach (var nonlocal in new[] { "8.8.8.8", "127.0.0.1", "255.255.255.255", "0.0.0.0" })
    {
        Check(!PairingDiscovery.IsLocalPeer(Ip(nonlocal)), "nonlocal peer allowed");
        await Throws<ArgumentException>(() => PairingDiscovery.ConnectAsync(nonlocal, CancellationToken.None));
    }
    var source = LoopbackScope("127.0.0.2");
    await Throws<ArgumentException>(() => PairingDiscovery.ConnectAsync("public.example", () => [source],
        (_, _) => Task.FromResult(new[] { Ip("8.8.8.8") }), 1, TimeSpan.FromSeconds(1), CancellationToken.None));
    var stalled = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    await Throws<TimeoutException>(() => PairingDiscovery.ConnectAsync("blocked.example", () => [source],
        (_, _) => stalled.Task, 1, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await Throws<OperationCanceledException>(() => PairingDiscovery.ConnectAsync("blocked.example", () => [source],
        (_, _) => stalled.Task, 1, TimeSpan.FromSeconds(2), cancel.Token));
}
