# Storage for Windows — local vertical slice

## What is here

- `Storage.Client`: logical C# SDK, bounded JSON metadata frames and streamed bytes; no Rust/repository dependency. Callers provide streams, logical IDs and revision preconditions, never repository/source paths or actor JSON.
- `Storage.Service`: independently runnable per-user Windows background/tray host, without a console or taskbar window. Owns `%LOCALAPPDATA%\Everyonesoftware\Storage\repository`, calls the **actual** `storage_execute(root, trustedActorJson, commandJson)` / `storage_free` ABI, and stages uploads privately. The root and fixed local-owner actor are constructed here, not accepted over IPC.
- `Storage.Browser`: native WinUI 3, runtime-loaded embedded XAML, Music-style native drag title area and caption buttons. Neutral slate + blue/teal product styling. File picker and drag/drop import, local search, grid/list, generated attributes, item/policy revisions, properties, immutable version history, historical/current export, new versions, rename, collection creation/membership and bounded UTF-8 text preview (text/*, at most 64 KiB). All displayed items and counts come from the service. Settings has a user-initiated Velopack update flow: check, download with progress, then explicit restart/install. It displays the real assembly informational version and never reports a failed network/integrity check as up-to-date.
- `Storage.Tests`: dependency-free .NET 10 executable assertions. Linux named-pipe harness uses the same framing, dispatcher, SDK and real native library. It deliberately does **not** exercise or impersonate Windows production identity checks.

`contracts/first-slice.md` is the integration authority. The internal core ABI is not the public wire protocol. This build is local-only: no device pairing, replication, remote authority, placement policy or saved-query claims.

## Security and authorization (important)

The production host is Windows-only. Before reading request JSON it:

1. Creates a first-instance, overlapped named pipe with `PIPE_REJECT_REMOTE_CLIENTS` and a protected DACL granting the current Windows SID only.
2. Obtains the connected client PID from the pipe, opens/holds its OS process handle, and resolves package family and executable via Windows APIs.
3. Allows the bundled `Storage.Browser.exe` only when its nonempty OS package family matches the service's own OS package family. This is an installed-package identity boundary, not a client-supplied app name or path grant.
4. Denies unknown packages and **all unpackaged clients by default**. No third-party enrollment endpoint exists.

The Browser also checks the connection-derived server process path against its deployed `Service\Storage.Service.exe`; packaged Browser additionally requires matching package family. The SDK uses identification-level impersonation and current-user-only local connection options. Public operations/argument names are allowlisted and reconstructed, with strict field matching. `sourcePath`, root and actor fields cannot be tunneled. Core authorization remains in force for item operations and current-policy historical reads.

**Installer tooling:** `scripts/build-windows-installer.ps1` performs native Windows self-contained publishing and pinned **Velopack 1.2.161** packaging with a separate nested service and actual native DLL. It requires clean committed release inputs, full Git history, exact count-derived version/source and explicit `-AllowUnsigned`. This checkout has not been installed or launched on Windows by the Linux validation. Published installers with the old placeholder Settings need a one-time manual in-place upgrade to an updater-enabled release; do not uninstall or clear repository/consent state. Unsigned binaries are not publisher-verified and may trigger Windows security warnings.

**Installed first-party consent:** the installed Browser starts its fixed bundled service with `--approve-bundled-browser` on first use. That flag supplies **no executable path or hashes**. The service derives only `../Storage.Browser.exe` from its own `current/Service` installation, shows its own **default-No** Yes/No dialog, and persists the approved exact path plus EXE/DLL SHA-256 under `%LOCALAPPDATA%\Everyonesoftware\Storage\service-config\bundled-browser-grant.json`. This current-user-only ACL directory is separate from repository data and outside the update payload. Denial saves no grant; changed/missing bundle bytes, linked targets or malformed state deny. An upgraded bundle requires new consent. OS-derived caller/peer identity checks still apply; this does not create a third-party or generic same-user allowance. A running locked service must be stopped and restarted with the explicit approval flag; the Browser error explains that approval is required.

**Development enrollment:** `start-development.ps1 -EnrollDevelopmentBrowser` explicitly grants one exact absolute Browser EXE path plus SHA-256 of its EXE and managed DLL for that service host lifetime. Changes require service restart and new explicit enrollment. The Browser cannot enroll itself; installed autostart can request only the service-owned consent dialog, never supply a grant. This is a narrow development allowance, **not** cryptographic isolation of arbitrary full-trust applications. Shared .NET dependencies/runtime and user-owned files remain writable within that OS user's trust boundary. The per-user service, unpackaged grant and repository ACL are not a sandbox against same-user full-trust malware or an administrator; package-identity checks do not change that limitation. No unrestricted “same user = authorized application” fallback exists.

Uploads have a ready handshake, stream exact declared lengths into random service-owned `.partial` files, flush, then call core commit. Interrupted or rejected uploads remove staging and never call core commit; startup removes abandoned `.partial` files. Exports expose only version metadata and bytes, not the core-resolved path; the SDK independently verifies exported SHA-256. Browser save-picker exports use a transacted write and commit only after verified complete transfer. Limits: 1 MiB metadata, 1 GiB content, one active service session, five-minute operation/session timeout; core local listing is capped at 500 and truncation is shown. A client may retain already delivered plaintext; there is no claim of recalling exports.

## Reproducible Linux gates

Known SDK in this environment: `/opt/data/tools/dotnet/dotnet` 10.0.404. WinUI is pinned to the cached, proven `Microsoft.WindowsAppSDK.WinUI` **1.8.260803003**. There are no fabricated WinRT/core stubs.

```bash
cd /opt/data/code/storage
source /opt/data/tools/storage-env.sh
bash apps/windows/validate-linux.sh
# Or pass another explicitly built host library:
bash apps/windows/validate-linux.sh "$PWD/artifacts/native/linux-x64/libstorage_core.so"
```

The validation script requires the real native `.so`; a missing core is a failure, not a substituted fake. It records output in `apps/windows/validation/` and compiles the actual service for Windows x64 and Browser against real WinUI/SDK packages. It sets invariant globalization for this host's missing ICU. The tests also parse the actual shell XML and validate literal typed named-control lookups.

Independent commands:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 /opt/data/tools/dotnet/dotnet run \
  --project apps/windows/Storage.Tests -- "$PWD/artifacts/native/linux-x64/libstorage_core.so"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 /opt/data/tools/dotnet/dotnet build \
  apps/windows/Storage.Service/Storage.Service.csproj -r win-x64 --self-contained true
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 /opt/data/tools/dotnet/dotnet build \
  apps/windows/Storage.Browser/Storage.Browser.csproj \
  -p:OutputType=Library -p:WindowsAppSDKSelfContained=false -p:SelfContained=false \
  -p:PrepareForRunDependsOn=CopyFilesToOutputDirectory
```

The last command is a **C# compile-only gate**, not an executable/deployment. The override bypasses Linux-incompatible MRT/PRI work only for that invocation. Native Windows defaults retain self-contained WinUI/runtime and **PRI generation enabled**; `resources.pri` is required at publish. Theme resources are merged on launch, not prematurely in the `Application` constructor.

### Windows application icon

The approved **Folder + engine** family uses the existing teal folder for Browser and two pale storage bays on the same teal tile for Service. Each executable embeds its own `Assets/Storage.ico`, with 16–256 px images for taskbar and high-DPI sizes. Browser applies its folder through `AppWindow.SetIcon` and uses the matching generated `Assets/Storage.png` in the title bar, empty state and About panel. Service uses its engine ICO for the notification area, popup/header and window class. Both runtime ICOs and the Browser PNG are copied on build/publish and required by installer payload validation. WinUI gets exactly one Content item per asset to avoid duplicate PRI entries. Storage-bay dots are fixed artwork, not live service-health indicators.

Regenerate Browser's ICO/PNG with `node scripts/generate-windows-icons.mjs` and Service's ICO with `node scripts/generate-windows-icons.mjs --application service`. The dependency-free generator reads each Android application's corresponding `ic_storage.xml` and rejects unsupported artwork changes rather than silently producing stale icons. `node --test tests/windows-release.test.mjs tests/windows-tray.test.mjs` checks deterministic regeneration, decodes every ICO size, distinguishes folder/engine pixels and protects executable/window/publish wiring. Actual taskbar/tray appearance and existing pinned-shortcut behavior still require native Windows verification after installing the changed build.

TDD evidence: framing, public argument validation/command construction, authorization decision policy and pipe/SDK/native integration were introduced after observed missing-API failures; export SHA verification and wrong-type revision validation had observed RED failures before implementation/fix. Tests are an executable console harness and must be run with `dotnet run`, not `dotnet test` (which would not execute these assertions).

## Build and run on native Windows

Prerequisites: .NET 10 SDK, Windows build tools/SDK suitable for WinUI resource processing, and the actual Windows x64 Rust DLL built by the repository core toolchain at `artifacts/native/windows-x64/storage_core.dll` (plus any native dependencies that toolchain requires). Native compilation, loader dependencies and Windows runtime validation are separate gates.

From PowerShell:

```powershell
# Native Windows publish; copies real engine beside the independent service.
./apps/windows/build.ps1
# Review the security warning above, stop any existing service, explicitly opt in:
./apps/windows/start-development.ps1 -EnrollDevelopmentBrowser
```

Deployment layout:

```text
apps/windows/artifacts/Browser/
  Storage.Browser.exe / .dll and self-contained .NET/WinUI payload
  resources.pri
  Service/
    Storage.Service.exe / .dll and self-contained .NET payload
    storage_core.dll
```

The service can be run independently (without grant arguments it remains locked for unpackaged callers):

```powershell
./apps/windows/artifacts/Browser/Service/Storage.Service.exe
# Click the tray icon for Start/Stop, Restart or Quit.
# Or exit this installation's host gracefully (also used by update/uninstall):
./apps/windows/artifacts/Browser/Service/Storage.Service.exe --stop
```

For manual explicit development enrollment, calculate both real file hashes and use:

```powershell
$browser = (Resolve-Path ./apps/windows/artifacts/Browser/Storage.Browser.exe).Path
$exe = (Get-FileHash $browser -Algorithm SHA256).Hash
$dll = (Get-FileHash ([IO.Path]::ChangeExtension($browser, '.dll')) -Algorithm SHA256).Hash
& ./apps/windows/artifacts/Browser/Service/Storage.Service.exe --dev-browser $browser --dev-sha256 $exe --dev-assembly-sha256 $dll
```

## Installer packaging and CI lifecycle gate

### Background service and tray controls

The native tray popup follows the selected **Status + menu / Header shortcut** design: Storage icon and service state in the header, an icon-only **Open Storage** shortcut at its right, then **Stop service / Start service**, **Restart service**, and **Quit Storage Service**. There is no explanatory footer. Startup does not open a console or popup; left/right tray activation opens the controls. The launcher has an accessible name and tooltip.

- **Stop** cancels active service sessions, waits for the engine to be released, and retains the tray host so **Start** remains available. **Restart** completes that shutdown before reopening the same repository. Transitions reject overlapping lifecycle actions; failures remain visible and retryable rather than reporting a false Running state.
- **Quit** asks for confirmation, stops the service and removes the icon. The existing installation-scoped `--stop` command and Velopack update/uninstall hooks exit the entire tray host without a confirmation prompt. Neither path deletes repository or approval data.
- Windows logoff/shutdown requests cancellation and waits up to four seconds for service draining inside the session-ending handler, before returning to Windows. A deadline or cleanup failure does not report a successful drain. Forced OS termination can still interrupt work; native session-end acceptance remains required.
- The header shortcut opens only the bundled `../Storage.Browser.exe`. A repeated launch activates the existing Browser rather than opening another window. Closing Browser leaves the service running. Launching from the tray does not silently restart a stopped service; start it explicitly from the tray and refresh Browser.
- First-party consent and pinned development enrollment remain service-owned. Launching Browser is not authorization and never changes a grant. Unpackaged installed bundles still require explicit approval. This is a user-session background app, not a LocalSystem Windows service or a new sign-in-startup registration.

Portable lifecycle/activation tests run through `Storage.Tests`; `tests/windows-tray.test.mjs` protects packaging and CI wiring, including the GUI PE subsystem and required service icon. These tests and a Linux Windows-target compile are not native tray, foreground activation, Explorer-restart, keyboard or high-DPI acceptance. Exercise those on an unlocked Windows desktop before publication.

On a **clean, committed native Windows checkout** (not this unborn local checkout):

```powershell
$release = node scripts/release.mjs version | ConvertFrom-Json
./scripts/build-windows-installer.ps1 -Version $release.version -SourceCommit $release.sha `
  -NativeDll artifacts/native/windows-x64/storage_core.dll -OutputDirectory dist/windows -AllowUnsigned
./scripts/test-windows-installer.ps1 `
  -InstallerPath "dist/windows/StorageWindows-$($release.version)-win-x64-Setup.exe" -EvidenceDirectory artifacts/windows-smoke
```

The Windows CI job builds Rust **natively with x64 MSVC**, not the Linux cross-build DLL, before invoking this CLI. Packaging executes the actual `Storage.Tests` harness with that DLL, publishes self-contained Browser and Service with release version/source metadata, requires native WinUI runtime and `resources.pri`, and hashes **every recursive published payload file against its `lib/app/` ZIP entry**. It disables vpk's default exclusions so symbols/runtime diagnostics cannot disappear silently. Only the pinned tool's `sq.version`, `Squirrel.exe` and `Storage_ExecutionStub.exe` additions are allowed. The flat six-file inventory is exactly `contracts/release-automation.md`; common tooling owns the platform manifest and aggregate checksum, not this script. The tool manifest and Browser SDK both pin Velopack 1.2.161.

The lifecycle gate has bounded operations and runs beneath the workflow's **five-minute hard step timeout**. It refuses an existing Storage install/profile/process, creates a task-owned custom install directory, runs the real setup, verifies registration and its shortcut, launches the installed Browser, approves **only that installation's service-owned consent dialog through UI Automation**, requires realized nonzero-geometry connected/empty-state controls, captures real foreground pixels with DWM bounds, and invokes the real installed Browser's production-authorized `--service-health` diagnostic. It then gracefully stops/restarts the independent service without consent arguments and requires the same service/store IDs and durable grant. No development authorization bypass or simulated health exists. Uninstall must independently remove files/registration/shortcuts/processes while leaving the repository/grant state intact. Cleanup removes only data proved absent at preflight and generated by this test; cleanup errors fail the gate. A locked/headless runner fails closed, not green.

`Storage.Browser` calls `VelopackApp.Build().SetAutoApplyOnStartup(false)` before WinUI initialization. Fast update/uninstall hooks request graceful shutdown of only its exact bundled service, preventing native-DLL locks. Independently, `Storage.Service.exe --stop` requests the same installation-scoped graceful stop. Uninstall never removes personal repository or consent state. Existing releases already use Velopack, so the transition is an in-place installer upgrade, not a legacy-installer migration.

### In-app updates

The native Settings view follows **Storage, refined**: compact update/version cards,
bottom-anchored Settings navigation and no item inspector while Settings is active.
Local service status/counts and collapsed technical/security details remain available.
**What’s new** loads the exact installed assembly source → offered manifest source
range on demand, not the release-description body or guessed version tags. Complete,
bounded compare and per-commit file pagination preserves shared changes and original
subjects/hashes. Missing source/history or notes failures expose retry without
disabling a valid download. The short installed version stays readable; the full
informational version is available in Technical details.

Settings uses Velopack **1.2.161** and the existing `StorageWindows` / `win-x64` identity. Nothing checks, downloads or applies on startup. Choose **Check for updates**, then **Download update** (with progress), then **Restart and install**. Download alone never requests installation. Uninstalled development builds explain that an installed build is required, without contacting the release feed. Failures are retryable and never shown as “up to date.” The installed version comes from assembly informational metadata, including the release source identity.

The public, cookie/credential-independent transport explicitly pages the GitHub release inventory (100 entries/page, ten-page hard cap); it does not assume GitHub Latest includes Windows. Stable Windows publications require `storage-windows-release.json`. Selection uses the highest numeric Windows version, binds all assets to that exact release, validates the complete platform inventory and feed hash, then validates the full-package size/SHA-256 against both manifest and feed before Velopack receives it. The backend independently reopens the final package in the same SDK locator's `PackagesDir` and checks the offered size/SHA-256 after `DownloadUpdatesAsync` (including its existing-file fast path) and immediately before `ApplyUpdatesAndRestart`. Changed or missing cached bytes cannot enable installation; restart integrity failures clear the Ready state. Malformed present metadata, unexpected origins, missing/duplicate payloads and inventory truncation fail rather than silently falling back. Downgrades and deltas are disabled. This verifies publication integrity, **not publisher signing**: Windows releases remain unsigned.

Restart refuses an in-progress Browser service operation. The existing exact-bundled-service fast update/uninstall shutdown hooks remain unchanged. Repository and consent paths are outside update payloads; no updater code reads or deletes them. A changed Browser bundle still requires the service's explicit approval. Existing published placeholder versions require one manual in-place installer upgrade after an updater-enabled release is published.

`Storage.Update.Tests` links the actual production controller/backend into a portable executable harness. The existing `Storage.Tests` entrypoint runs this suite automatically, so both Linux validation and native installer packaging execute it. Run it independently with `dotnet run --project apps/windows/Storage.Update.Tests`; add `-- --live` for an optional read-only production-inventory/manifest/feed check (no package download or install). `node --test tests/windows-updates.test.mjs` protects Settings, assembly-version, runtime-XAML and lifecycle wiring. Fixture package bytes are test-only, not installable production packages. Compilation and these checks do **not** establish native Settings interaction or a real installed old-to-new upgrade; those remain Windows acceptance gates.

Portable tooling checks: `node --test tests/windows-release.test.mjs` uses real PowerShell parsing and executable recursive-archive corruption/missing/duplicate/traversal checks, plus non-Windows fail-closed execution. PowerShell 7.2+ must be installed (`PWSH` can name an explicit executable); absence is an actionable failure, never a fake parser pass. These are tooling checks, not installed Windows acceptance.

## Remaining native acceptance gates

No Windows window, Windows pipe ACL/PID/package API, file picker, drag/drop, save transaction, native caption interaction, high-DPI rendering or Windows native DLL load was launched in the Linux execution environment. PowerShell publish/development-start scripts have not been executed on Windows. The compile gate does not establish native XAML template/property resolution or that an unsigned deployment is installable. No GUI screenshots or signed package/installer success are claimed.

On Windows verify: independent service startup and missing-DLL error; denied unregistered process before any repository operation; explicitly enrolled Browser only; package identity continuity once actual packaging is introduced; first import via picker and drop; empty/loading/failure state; search/grid/list; inspector revisions/hash/access caveat; new immutable version and historical byte-identical export; rename conflict/refresh; collections; service restart persistence; interrupted import cleanup; corruption export failure and transactional destination preservation; theme/titlebar/system buttons/keyboard/high-DPI; Settings no-update/error/available/download-progress/explicit restart states; installed old-to-new upgrade with unchanged repository IDs/content and renewed explicit Browser consent when hashes change.

Access inspector shows **real policy ID and revision**, the fixed local-owner context and new-item default-private semantics. The frozen API has no `getAccessPolicy`; it cannot enumerate current readers, so the UI explicitly says this rather than inventing a current-private result. Metadata properties are shown as returned; editing generated attributes is not offered.
