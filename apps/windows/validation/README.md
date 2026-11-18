# Windows validation evidence

## Executed successfully in Linux/WSL

- SDK: `/opt/data/tools/dotnet/dotnet` reported **10.0.404**.
- Built the real shared core with `cargo build --locked -p storage-core --target-dir apps/windows/.native-test-build`. This isolated target directory kept this worker's generated files within `apps/windows/` and avoided other workers' build outputs.
- Ran `bash apps/windows/validate-linux.sh /opt/data/code/storage/apps/windows/.native-test-build/debug/libstorage_core.so`.
- **29 executable assertions passed**, no skips or failures. `logic-transport.txt` is the actual output. Coverage includes runtime-XAML named controls, bounded framing, export SHA mismatch, invalid public arguments, deny-by-default package/development decision policy, real named-pipe transport, interrupted upload cleanup, native C ABI import/list/version/history/export, independent access-policy revision, collections, revision conflict, staging cleanup and repository reopen persistence.
- Real `Storage.Service.csproj` compiled Windows x64/self-contained: **zero warnings, zero errors** (`service-compile.txt`).
- Real `Storage.Browser.csproj` compiled against pinned WinUI and real project references using documented Linux compile-only overrides: **zero warnings, zero errors** (`winui-compile-only.txt`).
- `inspect-artifacts.mjs` verified actual AMD64 PE headers for service apphost and Browser assembly, plus the embedded runtime-shell resource name (`artifact-check.txt`). This is not native theme-resource resolution.

Tested native library SHA-256: `9b8ed1514b0af19619c237f043a84f9e8f881e10d9f84defec69589b66b09dad`.

TDD RED outputs were observed before implementing the missing framing API; strict public mapper/contracts; authorization decision policy; SDK/service/native session; export SHA verification; and SDK access-policy/remove/delete-collection methods. Wrong JSON revision type additionally reproduced a real `InvalidOperationException` before the numeric-kind guard fixed it. No tests use a fabricated core response to pass integration.

## Deliberately not claimed

- Windows app/native DLL launch, OS pipe security/PID/package identity runtime, titlebar behavior, file pickers, drag/drop, save transactions, high-DPI or XAML template resolution.
- Windows publish/PowerShell script execution, merged PRI production generation, signed MSIX/installer, updater publication.
- Authorization of an unpackaged Browser by default. It remains denied; `start-development.ps1 -EnrollDevelopmentBrowser` is explicit EXE+DLL hash enrollment for an independent development console service only. No production packaging success is implied.
- Linux named-pipe integration does not fake or exercise Windows identity APIs. Policy fixtures test policy decisions only. Native Windows denied-app acceptance is still required.
- Device networking, replication, saved queries, current reader enumeration or placement policy behavior.

See `../README.md` for exact build/run commands and native acceptance checklist. Generated binaries and isolated Rust target output are ignored, not source deliverables.
