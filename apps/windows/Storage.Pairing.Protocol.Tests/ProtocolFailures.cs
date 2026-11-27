using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Storage.Service.Pairing;

internal sealed class ProtocolFailures(X509Certificate2 hostIdentity, X509Certificate2 joinIdentity, Guid network,
    Func<CancellationToken, Task<(TcpClient Host, TcpClient Join)>> connect)
{
    public static async Task RunAsync(X509Certificate2 host, X509Certificate2 join, Guid network,
        Func<CancellationToken, Task<(TcpClient, TcpClient)>> connect)
    {
        var tests = new ProtocolFailures(host, join, network, connect);
        await tests.DeclineAsync(true);
        await tests.DeclineAsync(false);
        Console.WriteLine("PASS either peer can decline while the other prompt is pending; no trust saved");
        await tests.CancelAsync();
        Console.WriteLine("PASS cancellation interrupts both confirmations without trust");
        await tests.CancelStalledHandshakeAsync();
        Console.WriteLine("PASS cancellation interrupts a live stalled TLS handshake");
        await tests.RejectSelfAsync();
        Console.WriteLine("PASS identical device certificate rejected");
        await tests.RejectMissingCertificateAsync();
        Console.WriteLine("PASS TLS client without a certificate rejected");
        await tests.RejectMissingProtocolAsync();
        Console.WriteLine("PASS mutual TLS without pairing ALPN rejected before application frames");
        await tests.RejectFrameAsync([2, 255], "Unexpected pairing message", SslProtocols.Tls12);
        await tests.RejectFrameAsync([3, 2, 1, 65], "Unexpected pairing message");
        Console.WriteLine("PASS oversized frame rejected before body arrives; out-of-sequence frame rejected");
        await tests.RejectFrameAsync([2, 2, 1, 10], "Invalid pairing display name");
        await tests.RejectFrameAsync([2, 2, 1, 255], "Invalid pairing display name encoding");
        Console.WriteLine("PASS control characters and malformed UTF-8 rejected");
        await tests.RejectCommitmentAsync();
        Console.WriteLine("PASS wrong nonce commitment rejected before prompting");
        await tests.InterruptedSaveAsync();
        Console.WriteLine("PASS reciprocal save interruption reports local-only trust");
    }

    private async Task DeclineAsync(bool hostDeclines)
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        var hostPrompt = new Prompt();
        var joinPrompt = new Prompt();
        var hosting = Host(host, clock.Token, hostPrompt.Show);
        var joining = Join(join, clock.Token, joinPrompt.Show);
        await Task.WhenAll(hostPrompt.Shown.Task, joinPrompt.Shown.Task).WaitAsync(clock.Token);
        (hostDeclines ? hostPrompt : joinPrompt).Decision.SetResult(false);
        await RejectAsync<OperationCanceledException>(hosting, clock.Token, "declined");
        await RejectAsync<OperationCanceledException>(joining, clock.Token, "declined");
        Check((hostDeclines ? joinPrompt : hostPrompt).Token.IsCancellationRequested, "Waiting peer prompt was not cancelled");
    }

    private async Task CancelAsync()
    {
        using var clock = Deadline();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(clock.Token);
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        var hostPrompt = new Prompt();
        var joinPrompt = new Prompt();
        var hosting = Host(host, operation.Token, hostPrompt.Show);
        var joining = Join(join, operation.Token, joinPrompt.Show);
        await Task.WhenAll(hostPrompt.Shown.Task, joinPrompt.Shown.Task).WaitAsync(clock.Token);
        operation.Cancel();
        await RejectAsync<OperationCanceledException>(hosting, clock.Token);
        await RejectAsync<OperationCanceledException>(joining, clock.Token);
        Check(hostPrompt.Token.IsCancellationRequested && joinPrompt.Token.IsCancellationRequested, "Confirmation wait leaked");
    }

    private async Task RejectSelfAsync()
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        var hosting = Host(host, clock.Token);
        var joining = PairingProtocol.JoinAsync(join, hostIdentity, "Another name", UnexpectedPrompt, UnexpectedSave, clock.Token);
        await RejectAsync<AuthenticationException>(hosting, clock.Token, "itself");
        await RejectAsync<AuthenticationException>(joining, clock.Token, "itself");
    }

    private async Task CancelStalledHandshakeAsync()
    {
        using var clock = Deadline();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(clock.Token);
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        var joining = Join(join, operation.Token, UnexpectedPrompt);
        // Read a real TLS record before cancelling, not just the initial token check.
        var tlsHeader = new byte[5];
        await host.GetStream().ReadExactlyAsync(tlsHeader, clock.Token);
        Check(tlsHeader[0] == 22 && tlsHeader[1] == 3, "Expected a TLS handshake record");
        operation.Cancel();
        await RejectAsync<OperationCanceledException>(joining, clock.Token);
    }

    private async Task RejectMissingCertificateAsync()
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        using var tls = new SslStream(join.GetStream());
        var hosting = Host(host, clock.Token);
        try { await tls.AuthenticateAsClientAsync(ClientOptions(null), clock.Token); }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        { /* Provider may reject the client side as well. */ }
        await RejectAsync<AuthenticationException>(hosting, clock.Token);
    }

    private async Task RejectFrameAsync(byte[] malformed, string message,
        SslProtocols versions = SslProtocols.Tls12 | SslProtocols.Tls13)
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        using var tls = new SslStream(join.GetStream());
        var hosting = Host(host, clock.Token);
        var options = ClientOptions(joinIdentity);
        options.EnabledSslProtocols = versions;
        await tls.AuthenticateAsClientAsync(options, clock.Token);
        Check(tls.IsEncrypted && tls.IsMutuallyAuthenticated, "Malformed frame test requires real mutual TLS");
        await ReadFrameAsync(tls, 1, clock.Token);
        await tls.WriteAsync(malformed, clock.Token);
        await RejectAsync<InvalidDataException>(hosting, clock.Token, message);
    }

    private async Task RejectMissingProtocolAsync()
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        using var tls = new SslStream(join.GetStream());
        var hosting = Host(host, clock.Token);
        var options = ClientOptions(joinIdentity);
        options.ApplicationProtocols = null;
        try
        {
            await tls.AuthenticateAsClientAsync(options, clock.Token);
            Check(tls.IsEncrypted && tls.IsMutuallyAuthenticated, "Peer had to present a valid TLS client certificate");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        { /* A provider may reject missing ALPN during TLS rather than after the handshake. */ }
        await RejectAsync<AuthenticationException>(hosting, clock.Token);
    }

    private async Task RejectCommitmentAsync()
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        using var tls = new SslStream(join.GetStream());
        var hosting = Host(host, clock.Token);
        await tls.AuthenticateAsClientAsync(ClientOptions(joinIdentity), clock.Token);
        await ReadFrameAsync(tls, 1, clock.Token);
        await tls.WriteAsync(new byte[] { 2, 2, 1, 65 }, clock.Token);
        await ReadFrameAsync(tls, 3, clock.Token);
        var commit = new byte[34];
        commit[0] = 4;
        commit[1] = 32;
        await tls.WriteAsync(commit, clock.Token);
        await ReadFrameAsync(tls, 5, clock.Token);
        var reveal = new byte[34];
        reveal[0] = 6;
        reveal[1] = 32;
        await tls.WriteAsync(reveal, clock.Token);
        await RejectAsync<AuthenticationException>(hosting, clock.Token, "commitment");
    }

    private async Task InterruptedSaveAsync()
    {
        using var clock = Deadline();
        var (host, join) = await connect(clock.Token);
        using var h = host;
        using var j = join;
        PairedDevice? local = null;
        var hosting = Host(host, clock.Token, Accept, peer => local = peer);
        var joining = Join(join, clock.Token, Accept, _ => throw new IOException("Test store unavailable"));
        await RejectAsync<PairingUncertainException>(joining, clock.Token, "host saved trust");
        join.Dispose();
        await RejectAsync<PairingUncertainException>(hosting, clock.Token, "Local trust was saved");
        Check(local is not null, "Host must have completed its own write before reporting partial trust");
    }

    private Task Host(TcpClient client, CancellationToken ct,
        Func<PairingChallenge, CancellationToken, Task<bool>>? confirm = null, Action<PairedDevice>? save = null) =>
        PairingProtocol.HostAsync(client, hostIdentity, "Host machine", network, confirm ?? UnexpectedPrompt, save ?? UnexpectedSave, ct);

    private Task Join(TcpClient client, CancellationToken ct,
        Func<PairingChallenge, CancellationToken, Task<bool>> confirm, Action<PairedDevice>? save = null) =>
        PairingProtocol.JoinAsync(client, joinIdentity, "Join machine", confirm, save ?? UnexpectedSave, ct);

    private static SslClientAuthenticationOptions ClientOptions(X509Certificate2? identity) => new()
    {
        TargetHost = "storage-pairing",
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ApplicationProtocols = [new SslApplicationProtocol("storage-pairing/1")],
        AllowTlsResume = false,
        CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck },
        ClientCertificateContext = identity is null ? null : SslStreamCertificateContext.Create(identity, null, offline: true),
        RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
    };

    private static async Task ReadFrameAsync(SslStream stream, byte type, CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);
        Check(header[0] == type, "Unexpected response frame in test");
        await stream.ReadExactlyAsync(new byte[header[1]], ct);
    }

    private static async Task RejectAsync<T>(Task task, CancellationToken deadline, string? message = null) where T : Exception
    {
        try { await task.WaitAsync(deadline); }
        catch (T ex) when (!deadline.IsCancellationRequested && (message is null || ex.Message.Contains(message, StringComparison.Ordinal)))
        {
            return;
        }
        throw new InvalidOperationException($"Expected pairing failure {typeof(T).Name}");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private static Task<bool> UnexpectedPrompt(PairingChallenge challenge, CancellationToken ct) =>
        throw new InvalidOperationException("Invalid peer must not reach user approval");

    private static void UnexpectedSave(PairedDevice peer) => throw new InvalidOperationException("Invalid pairing must not persist trust");
    private static Task<bool> Accept(PairingChallenge challenge, CancellationToken ct) => Task.FromResult(true);
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(10));

    private sealed class Prompt
    {
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public Task<bool> Show(PairingChallenge challenge, CancellationToken ct)
        {
            Token = ct;
            Shown.SetResult();
            return Decision.Task.WaitAsync(ct);
        }
    }
}
