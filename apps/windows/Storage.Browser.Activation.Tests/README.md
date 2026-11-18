# Browser activation checks

Standalone portable command (no Service, Browser, or Storage.Tests build):

```sh
source /opt/data/tools/storage-env.sh
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run --project apps/windows/Storage.Browser.Activation.Tests
```

The same command was used for RED and GREEN. Initial RED raised `FAIL: tray launch service-autostart policy is missing`; the next coordination tracer raised `FAIL: single-instance owner/activation coordination is missing`. Restoration RED raised `FAIL: window restoration policy exists`. Identity/restoration references also produced missing-symbol compile failures before their helpers existed; these are not native runtime evidence. Final GREEN runs 21 behavior assertions against linked production activation files with an injected process/dispatch adapter, including minimized-window operation ordering.

Public integration API: `Storage.Browser.Activation.Tests.ActivationTestSuite.Run()` returns `Task` and throws on failure. The executable `Main()` delegates to that same suite. This project links only Browser activation sources, not duplicate Client types, and has no project references or package dependencies.

The Windows adapter is compiled by this portable project but is not invoked on Linux. Native acceptance still needs Windows: two simultaneous/repeated fixed-executable header launches, activation before WinUI readiness, minimized restoration/foreground, tray/normal launch while the service host is stopped or starting, close/reopen/crash recovery, independent `--service-health`, and packaged Velopack hook behavior. Foreground permission is a best-effort OS request; Windows ultimately controls foreground activation.

Native API references inspected during implementation:

- Microsoft Learn: `AllowSetForegroundWindow` (`DWORD` process ID; never `ASFW_ANY`).
- Microsoft Learn: `SetForegroundWindow`, `ShowWindow` (`SW_RESTORE = 9` only when `IsIconic`).
- Microsoft Learn: `NamedWaitHandleOptions` (explicit current-user and current-session access).

The named mutex is owned and released on Program.Main's STA. The current-user auto-reset wake-up event is created before ownership can be published, so signals before wait registration remain pending. The portable coordinator separately retains signals until the window and dispatcher attach. No activation payload can change service paths, enrollment, grants, or repositories.
