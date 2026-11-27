# Windows pairing discovery transport tests

From `apps/windows`, with the pinned .NET SDK:

```sh
dotnet run --project Storage.Pairing.Discovery.Tests
```

The harness links the real service transport sources without starting the Service. It checks same-subnet address policy, strict packet decoding, real UDP query/reply (directed broadcast on Linux), correlation and deduplication, and manual address resolution/connection over real TCP. Socket tests inject loopback scopes and ephemeral ports; production never accepts loopback and always uses UDP 48554 and TCP 48553. No DNS server, firewall changes, repository, or other device is needed to run the tests.

In production the service must own the advertiser's cancellation token and run it only during explicit Add mode. Discovery is a bounded IPv4 LAN hint, not authentication. Manual entry can be a dotted IPv4 address or a hostname that the OS resolves to an eligible, directly attached private/link-local IPv4 peer; URLs and custom ports are not accepted. Broadcast may be unavailable even when manual connections work. A returned TCP client belongs to the caller and must go through the pairing protocol before establishing trust. Native Windows two-device delivery, including the local firewall/network policy, requires a separate runtime check.
