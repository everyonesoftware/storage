# Windows validation evidence

## Folder + engine icons — November 19, 2026

- Observed failing tests for Service's own embedded/copied icon, separate engine generation and Browser's in-app folder binding before implementation. Also observed the limited renderer silently accept transformed artwork before adding fail-closed structure/attribute guards.
- `node --test tests/windows-release.test.mjs tests/windows-tray.test.mjs`: **28 passed**, zero failures/skips. Both role ICOs deterministically regenerate; every 16–256 px payload decodes, and representative engine bays/slots/dots differ from the folder. Unsupported groups, alpha, strokes and unknown roles are rejected.
- Ran the complete ordinary CI Node suite with `STORAGE_ANDROID_GRADLE_TESTS=1`: **105 passed**, zero failures/skips, including real Android release-configuration refusal checks.
- Ran `Storage.Tests` against the actual Linux core successfully. Rebuilt the Windows-target Service/self-contained apphost and Browser with the documented Linux compile-only overrides: both **zero warnings/errors**. Runtime XAML parses with matching typed control lookups.
- Evaluated Browser `Content,None`: exactly one Content item and zero None duplicates for each of `Assets/Storage.ico` and `Assets/Storage.png`. Both built runtime ICO copies match their respective source files.
- Independently decoded actual PE resources in Browser's managed assembly and Service's managed assembly/apphost: all **10 ICON payloads per PE** byte-match the appropriate role ICO, with GROUP_ICON present. Inspected rendered actual 16, 32 and 256 px folder/engine PNGs, not just the concept drawings.
- Rust workspace tests, formatting, warnings-denied Clippy and the real C ABI smoke suite passed. No core/lifecycle/authorization changes were made. Native Windows installed rendering, tray/taskbar/pinned shortcuts and PRI resource resolution remain unverified; no commit/push or publication performed.

## Previous Linux/WSL validation — historical

- SDK: `/opt/data/tools/dotnet/dotnet` reported **10.0.404**.
- Ran `bash apps/windows/validate-linux.sh /opt/data/code/storage/artifacts/native/linux-x64/libstorage_core.so` against the real shared core.
- **89 executable assertions passed**, no skips or failures. `logic-transport.txt` is the actual output. Coverage includes updater integrity, portable tray lifecycle/draining, single-instance Browser activation/service-start policy, runtime-XAML named controls, bounded framing, export SHA mismatch, invalid public arguments, deny-by-default package/development decision policy, real named-pipe transport, interrupted upload cleanup, native C ABI import/list/version/history/export, independent access-policy revision, collections, revision conflict, staging cleanup and repository reopen persistence.
- Real `Storage.Service.csproj` compiled Windows x64/self-contained: **zero warnings, zero errors** (`service-compile.txt`).
- Real `Storage.Browser.csproj` compiled against pinned WinUI and real project references using documented Linux compile-only overrides: **zero warnings, zero errors** (`winui-compile-only.txt`).
- `inspect-artifacts.mjs` verified actual AMD64 PE headers for service apphost and Browser assembly, plus the embedded runtime-shell resource name (`artifact-check.txt`). This is not native theme-resource resolution.
- Independently decoded the actual service apphost's PE optional header: subsystem **2 (Windows GUI)**, not a console apphost. Compared its copied `Assets/Storage.ico` byte-for-byte with the shared Browser artwork. The standalone installer gate now enforces that subsystem and required icon too.

Tested native library SHA-256: `0ebf8e7ae8bc5cef8d5d04abbfab2554785f5d2c9cd7cd2fecc54ce839227255`.

TDD RED outputs were observed before implementing the missing framing API; strict public mapper/contracts; authorization decision policy; SDK/service/native session; export SHA verification; and SDK access-policy/remove/delete-collection methods. Wrong JSON revision type additionally reproduced a real `InvalidOperationException` before the numeric-kind guard fixed it. No tests use a fabricated core response to pass integration.

## Deliberately not claimed

- Windows app/native DLL launch, OS pipe security/PID/package identity runtime, tray popup/icon rendering, physical Shell clicks, foreground/minimized-window activation, Explorer restart, titlebar behavior, file pickers, drag/drop, save transactions, high-DPI or XAML template resolution.
- Windows publish/PowerShell script execution, merged PRI production generation, signed MSIX/installer, updater publication.
- Authorization of an unpackaged Browser by default. It remains denied; `start-development.ps1 -EnrollDevelopmentBrowser` is explicit EXE+DLL hash enrollment for an independent development tray host only. No production packaging success is implied.
- Linux named-pipe integration does not fake or exercise Windows identity APIs. Policy fixtures test policy decisions only. Native Windows denied-app acceptance is still required.
- Device networking, replication, saved queries, current reader enumeration or placement policy behavior.

See `../README.md` for exact build/run commands and native acceptance checklist. Generated binaries and isolated Rust target output are ignored, not source deliverables.
