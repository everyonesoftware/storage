# Toolchain installation evidence

Installed for Storage on November 17, 2026. Installed toolchains live outside the checkout; no Python project tooling was introduced.

| Tool | Verified installed version | Location |
|---|---|---|
| Rust | rustc 1.100.0 / Cargo 1.100.0 | `/opt/data/tools/cargo/bin`, `/opt/data/tools/rustup` |
| Android NDK | 29.0.14206865; bundled Clang 21.0.0 | `/opt/data/tools/android-sdk/ndk/29.0.14206865` |
| LLVM-MinGW | release 20261104; bundled Clang 23.1.5 | `/opt/data/tools/llvm-mingw-20261104-ucrt-ubuntu-22.04-x86_64` |

Source `/opt/data/tools/storage-env.sh` to expose the existing Java/Android/.NET tools and the newly installed Rust/cross-compilation tools.

Rustup 1.29.3 was downloaded from Rust's official static distribution archive. Its SHA-256 was checked against the corresponding official `.sha256` file before execution. The installer requires the basename `rustup-init`; renaming the executable to an application-specific filename makes it select an invalid proxy mode. The installer warned about the shell HOME differing from the passwd home, so both `CARGO_HOME` and `RUSTUP_HOME` were set explicitly. No shell startup file was rewritten.

Rust installed targets: `aarch64-linux-android`, `x86_64-linux-android`, `x86_64-pc-windows-gnullvm`, `x86_64-pc-windows-gnu`, and host `x86_64-unknown-linux-gnu`. Builds use gnullvm for the LLVM-MinGW linker; the GNU target is installed but not required by the current build script. rustfmt and clippy were installed. The root `rust-toolchain.toml` pins the selected toolchain.

The Android NDK was installed using the existing SDK manager after accepting SDK licenses. The package manager's current warning recommends the newer Android CLI; installation succeeded using the installed manager. This is a toolchain installation, not proof that a native library or APK works.

LLVM-MinGW was downloaded from the upstream repository's release asset. Its SHA-256 matched the digest returned by GitHub's release API: `919380403de87dca3c581c2053230cd298af5e891286628ba0613b7fed370926`. It supplies Windows cross-compilation without requiring administrator access or system package installation.

Download provenance:

- Rust channel metadata: `https://static.rust-lang.org/dist/channel-rust-stable.toml`
- Rustup release metadata: `https://static.rust-lang.org/rustup/release-stable.toml`
- Rustup installer and checksum: `https://static.rust-lang.org/rustup/archive/1.29.3/x86_64-unknown-linux-gnu/rustup-init` and its `.sha256` suffix
- LLVM-MinGW release API: `https://api.github.com/repos/mstorsjo/llvm-mingw/releases/latest` (resolved to tag `20261104` during installation)
- Pinned LLVM-MinGW asset: `https://github.com/mstorsjo/llvm-mingw/releases/download/20261104/llvm-mingw-20261104-ucrt-ubuntu-22.04-x86_64.tar.xz`

## Native validation boundary

This environment has no `/dev/kvm`, so the task-owned Android emulator uses software emulation. Other applications' AVDs and the shared adb server must not be stopped. Native Windows launch remains a separate required gate; a Windows DLL or C# assembly built here is not proof of a successful Windows launch or installer.
