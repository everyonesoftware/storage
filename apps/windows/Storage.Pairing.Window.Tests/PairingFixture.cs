using System.Net;
using ZXing.Common;

namespace Storage.Service.Pairing;

internal sealed record PairingChallenge(string PeerName, string Code);
internal sealed record PairedDevice(string Name, bool IsCoordinator);
internal sealed record NearbyDevice(string Name, IPAddress Address);
internal sealed class PairingUncertainException(string message, Exception cause) : IOException(message, cause);

internal sealed class PairingManager
{
    internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Token;
    internal volatile bool FailRead;
    internal IReadOnlyList<PairedDevice> List() => FailRead ? throw new IOException() : [];
    internal Task HostAsync(Func<PairingChallenge, CancellationToken, Task<bool>> confirm, CancellationToken ct)
    {
        Token = ct;
        return Completion.Task; // Let the test inject the outcome after the window requests cancellation.
    }
    internal Task HostQrAsync(IPAddress address, Action<BitMatrix?> display,
        Func<PairingChallenge, CancellationToken, Task<bool>> confirm, CancellationToken ct) => throw new NotSupportedException();
    internal Task JoinAsync(string address, Func<PairingChallenge, CancellationToken, Task<bool>> confirm,
        CancellationToken ct) => throw new NotSupportedException();
}

internal static class PairingDiscovery
{
    internal static IReadOnlyList<IPAddress> LocalAddresses() => [];
    internal static Task<IReadOnlyList<NearbyDevice>> FindAsync(CancellationToken ct) => throw new NotSupportedException();
}
