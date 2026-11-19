# User-initiated application updates

## Scope and delivery

Application updates replace application binaries, not Storage Items, repositories or application grants. The native Browser Settings pages own the user-facing flow. Update transport is independent of Storage service credentials and peer replication. There is no background auto-install or downgrade.

Existing published versions with placeholder Settings cannot acquire this implementation themselves. After the first updater-enabled release is published, those installations require a one-time manual in-place upgrade: Windows installer; Android Service and Browser APKs signed with the existing release key. Never instruct users to uninstall or clear Service data to update. Debug-to-release signing changes cannot be made into in-place updates by this feature.

Implementation, compilation, emulator exercise, native Windows upgrade acceptance, physical-phone acceptance and publication are separate evidence gates. A working local implementation is not yet a published update.

## Release selection

- Read the public `everyonesoftware/storage` release inventory, with explicit bounded pagination. Do not assume GitHub Latest has both platforms.
- Ignore drafts and prereleases. Compare platform versions numerically, not lexically or by publication time.
- A platform is published only when its platform-specific manifest is present. Missing platform markers permit searching older releases; corrupt present publication metadata is an error, not permission to report up-to-date.
- Bind selected platform metadata and downloads to their original release. Preserve unchanged platforms' independent versions and source identities.
- Reject malformed/duplicate inventory, unsafe filenames, unexpected download origins, mismatched versions and incomplete platform payloads. Surface network, rate-limit and integrity failures honestly.

## Windows

Use the pinned Velopack SDK and existing installer/package identity. Flow: **Check for updates → Download update** with progress → **Restart and install**. Keep startup auto-apply disabled. Only enable install after a verified download and explicit user action. Preserve the existing exact-bundled-service shutdown hooks and repository/consent locations. Uninstalled development builds must not claim that an update can be applied.

The public feed and full package already form part of Windows release output; Settings must use actual native update APIs rather than launch a browser to the installer. The installed version display must come from build/installation metadata, not the old hardcoded local-slice label.

## Android

Both product packages participate: `com.everyonesoftware.storage.service` and `com.everyonesoftware.storage.browser`. Discover the manifest-backed Android publication, download into application-private staging with bounded transfer and progress, and validate byte length and SHA-256 before installation. Check archive package IDs, version codes and signing certificates against installed product identities before handing payloads to Android.

Use Android's package installer and user confirmation; never silently approve its prompts. Handle permission to install from this source and cancellation without losing repository data. Use one package session at a time on all supported Android versions: explicitly install Service first, verify its installed version, then offer a separate Browser install action and confirmation. Do not automatically advance or allow Browser to leap ahead of an unupdated Service. The API-29 atomic multi-package user-consent path failed real commit acceptance; session creation alone is not evidence that it works. Preserve staged-update continuity across the install UI and process recreation, with revalidation before reuse. Reject downgrades, certificate mismatch and unrelated packages. Resolve both real installed product versions; display the Browser's version in the compact Settings heading while checking Service independently for action eligibility.

## Settings presentation and change history

The native Settings panels use the approved compact Storage design. The installed
Browser version and explicit check action sit above a matched status card. An
available offer identifies its version separately from the installed version and
provides an initially collapsed **What’s new** disclosure. Windows retains local
service and collapsed technical/security information and hides the item inspector
only while Settings is active. Android Settings retains its bottom navigation but
omits the redundant application header/refresh action, Updates heading, package
version section, About Storage and Appearance sections. Removing those labels does
not remove Android's independent installed-package checks or Service-first consent.

Change history is loaded on demand, independently of package downloads. Its endpoints
are the installed platform's source identity and the offered platform manifest's
`sourceCommit`, not a guessed version/tag comparison or the release-description body.
Windows uses assembly informational source metadata. Android embeds a source only
for matching clean committed release inputs supplied by `storageSourceCommit`; older
release installations may resolve their existing publication manifest. Development
and explicitly unknown source identities must remain unavailable rather than describe
another build's history.

Compare and per-commit file responses are bounded and explicitly paginated. The
complete range, endpoints and ancestry must agree before presenting a change count.
Summaries group Improvements and Fixes, omit empty groups, and remove mechanical
subject prefixes without inventing claims. Exclude documentation, CI or other-platform
changes only when their complete changed-file set proves them irrelevant; retain
mixed/shared changes and consider both paths of a rename. Optional Commit details
retain all original subjects, hashes and the exact source range.

Loading, unavailable/retry and confirmed-empty history are distinct. A failed or
truncated history request cannot become a zero-change claim, disable an otherwise
valid download, or overwrite notes belonging to a replacement offer. Check, download,
verification and explicit platform installation remain separate states; no mockup
versions, calibration controls or simulated installer actions belong in production.

## Verification requirements

Test platform-specific selection with releases containing only the other platform, pagination, no update, rate/network errors, malformed publications, integrity failures and downgrade attempts. Test download/install state transitions and concurrent action refusal, not only helper parsing. Android additionally requires signature/package mismatch tests and installer-result handling. Native acceptance must exercise actual Settings and OS installation, including repository and authorization continuity; fixture packages and mocked responses must be identified as tests rather than production releases.
