# Portable tray behavior and native adapter checks

Run from the repository root after sourcing `/opt/data/tools/storage-env.sh`:

```sh
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
dotnet run --project apps/windows/Storage.Tray.Tests/Storage.Tray.Tests.csproj
node --test apps/windows/Storage.Tray.Tests/native-contract.test.mjs
dotnet build apps/windows/Storage.Tray.Tests/NativeCompile/NativeCompile.csproj -warnaserror
```

`Storage.Tray.Tests.TrayTestSuite.Run(): Task` is public and is called by the normal Storage.Tests executable. It links the production portable controller, popup placement/activation policy and Win32 data layouts without creating Windows handles. NativeCompile links the complete Service and Client sources into a private library output, avoiding shared Service/Browser/test build outputs.

## Test-first record

Each new behavior was exercised failing before the corresponding implementation:

- Start readiness: failed because status stayed Stopped; passed after awaiting real readiness.
- Startup failure/retry: timed out waiting on readiness; passed after racing/observing runner completion.
- Stop/restart serialization: timed out because no drain occurred; passed after cancellation, drain and action gating.
- Quit during startup: timed out because quit waited behind uncancelled startup; passed after terminal quit cancels before waiting and awaits disposal.
- OS session ending: the native source guard rejected a posted asynchronous quit; the new controller boundary was initially missing. The bounded synchronous boundary now waits for cancellation/disposal before returning success and reports an expired budget without claiming completion.
- Live runner failure: timed out waiting for Error; passed after immediate observation and change notification.
- Shell v4 unpacking: failed signed anchor/id assertion; passed after packed callback decoding.
- DPI/workarea placement: failed scaled-size assertion; passed after scale and workarea clamping.
- Browser launch policy: failed fixed sibling assertion; passed after Service-layout validation and fixed sibling resolution.
- Failed disposal: replacement was incorrectly started; passed after refusing replacement following a cleanup failure.
- Win64 structures: DRAWITEMSTRUCT size failed; passed after preserving its itemID field and native HDC alignment.

The three Node checks were red for console OutputType, the missing native adapter, and the old non-STA startup/readiness wiring; they are source/integration guards, not native runtime tests.

## Native acceptance contract

- Host/popup class: `Storage.Service.TrayHost`.
- Window title: `Storage Service` (initially hidden).
- Notification callback: `WM_APP + 1` (`0x8001`); icon ID: `1`; `NOTIFYICON_VERSION_4`.
- Native Button IDs/names: `101` Browse (tooltip: Open Storage Browser); `102` Start service / Stop service; `103` Restart service; `104` Quit Storage Service; `107` Launch on Startup (drawn as a switch, native auto-checkbox state).
- Native Static IDs: `105` Storage Service title; `106` actual lifecycle status and any failure detail. The normal popup is 304 × 251 logical pixels; no extra Status/helper row is reserved.
- On Windows, verify the Browse button and Startup switch at 100% and a scaled DPI. Use mouse and keyboard to open Browser and toggle Startup both ways; check the actual registration after each toggle and reopen the menu to verify display state.
- The real callback accepts NIN_SELECT, NIN_KEYSELECT and WM_CONTEXTMENU with the version-4 icon-ID packing. No test-only command-line activation exists.
- Production shortcut resolves only the sibling `Storage.Browser.exe` from the Service layout and passes only `--from-tray`.
- Icon content: Service's own engine ICO copied as `Assets/Storage.ico` and embedded as the Service application icon. The Browser retains its separate folder ICO; Service no longer links Browser artwork.

Windows native UI, accessibility-provider behavior, notification area appearance, Explorer restart, monitor/DPI transitions and installed shutdown still require actual Windows acceptance. Portable tests and linked-source compilation do not establish those outcomes.
