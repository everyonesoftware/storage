using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Storage.Service.Pairing;

internal readonly record struct PairingLanScope(IPAddress Address, int PrefixLength, int InterfaceIndex)
{
    private uint Mask => PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);
    internal IPAddress? Broadcast => PrefixLength < 31 ? FromNumber(Number(Address) | ~Mask) : null;

    internal bool ContainsHost(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || PrefixLength == 32) return false;
        uint network = Number(Address) & Mask;
        uint candidate = Number(address);
        return (candidate & Mask) == network &&
            (PrefixLength == 31 || (candidate != network && candidate != (network | ~Mask)));
    }

    internal static bool TryCreate(IPAddress address, int prefixLength, int interfaceIndex, out PairingLanScope scope)
    {
        scope = default;
        if (address.AddressFamily != AddressFamily.InterNetwork || interfaceIndex <= 0 ||
            prefixLength is < 1 or > 31) return false;
        uint ip = Number(address);
        // A reported mask must not extend past the private/link-local block.
        int minimumPrefix = ip switch
        {
            >= 0x0a000000 and <= 0x0affffff => 8,
            >= 0xac100000 and <= 0xac1fffff => 12,
            >= 0xc0a80000 and <= 0xc0a8ffff => 16,
            >= 0xa9fe0000 and <= 0xa9feffff => 16,
            _ => 33,
        };
        if (prefixLength < minimumPrefix) return false;
        var candidate = new PairingLanScope(address, prefixLength, interfaceIndex);
        if (!candidate.ContainsHost(address)) return false;
        scope = candidate;
        return true;
    }

    internal static IReadOnlyList<PairingLanScope> Current()
    {
        var scopes = new List<PairingLanScope>();
        if (!Socket.OSSupportsIPv4) return scopes;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                int index = properties.GetIPv4Properties().Index;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (OperatingSystem.IsWindows() && unicast.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred)
                        continue;
                    if (TryCreate(unicast.Address, unicast.PrefixLength, index, out var scope)) scopes.Add(scope);
                }
            }
            catch (NetworkInformationException) { /* Adapter disappeared while taking the snapshot. */ }
        }
        return scopes;
    }

    private static uint Number(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress FromNumber(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }
}
