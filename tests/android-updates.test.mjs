import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

test('Restart permission is scoped outside instrumentation and restored even on failure', () => {
 const source = read('validate-device.sh');
 const helper = source.match(/run_restart_tests\(\) \([\s\S]*?\n\)/)?.[0];
 assert.ok(helper, 'external restart permission boundary is required');
 const directory = mkdtempSync(join(tmpdir(), 'storage-restart-'));
 try {
  for (const status of [0, 1]) {
   const log = join(directory, `calls-${status}`);
   const script = `set -euo pipefail\nserial=owned\nadb() { printf '%s\\n' "$*" >> "$LOG"; if [[ "$*" == *'appops get'* ]]; then printf 'REQUEST_INSTALL_PACKAGES: default\\n'; fi; }\nrun_test() { printf 'instrument %s\\n' "$*" >> "$LOG"; return ${status}; }\n${helper}\nrun_restart_tests\n`;
   const result = spawnSync('bash', ['-c', script], { env: { ...process.env, LOG: log }, encoding: 'utf8' });
   assert.equal(result.status, status);
   const calls = readFileSync(log, 'utf8');
   assert.match(calls, /appops get .*REQUEST_INSTALL_PACKAGES[\s\S]*appops set .*REQUEST_INSTALL_PACKAGES allow[\s\S]*instrument updates-restart[\s\S]*appops set .*REQUEST_INSTALL_PACKAGES default/);
  }
 } finally { rmSync(directory, { recursive: true, force: true }); }
 assert.ok(source.indexOf('run_test updates-settings ') < source.lastIndexOf('\nrun_restart_tests'));
});

test('Native update card preserves approved title, pill and disclosure hierarchy', () => {
 const panel = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/UpdateSettings.kt');
 assert.match(panel, /Text\("Update available", style = MaterialTheme.typography.titleLarge/);
 assert.match(panel, /Button\(onClick = onDownload, enabled = enabled, modifier = Modifier.heightIn\(min = 48.dp\)\)/);
 assert.match(panel, /Icon\(if \(expanded\)[\s\S]*Text\("What’s new", Modifier.weight\(1f\)/);
 assert.match(panel, /Text\("Up to date"/);
 assert.match(panel, /Text\("Not checked yet"/);
});
const read = path => readFileSync(new URL(`../apps/android/${path}`, import.meta.url), 'utf8');
test('APK instrumentation fixtures never buffer entire packages in the emulator heap', () => {
 for (const name of ['UpdateInstallerTest', 'UpdateSettingsTest', 'UpdateRestartTest']) {
  const source = read(`browser/src/androidTest/java/com/everyonesoftware/storage/browser/updates/${name}.kt`);
  assert.doesNotMatch(source, /\.readBytes\(/, `${name} must stream fixtures larger than the CI heap`);
  assert.match(source, /fixtureSha256\(file\)/);
 }
});
test('Approved Android Settings removes the shell topbar and retired sections only on Settings', () => {
 const shell = read('browser/src/main/java/com/everyonesoftware/storage/browser/MainActivity.kt');
 assert.match(shell, /topBar = \{ if \(page != "Settings"\)/);
 assert.doesNotMatch(shell, /Section\("(?:About Storage|Appearance|Updates|Package versions)"\)/);
 const panel = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/UpdateSettings.kt');
 assert.match(panel, /Current Version:/);
 assert.match(panel, /Check now/);
 assert.match(panel, /What’s new/);
 assert.match(panel, /Commit details/);
 assert.doesNotMatch(panel, /Updates replace both|never uninstall|Android asks separately/);
});
test('Changelog and generated source identity are wired to the real controller', () => {
 const controller = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/UpdateController.kt');
 assert.match(controller, /fun loadNotes\(/);
 assert.match(controller, /installedSource\(/);
 assert.match(controller, /BUILD_SOURCE_COMMIT/);
 assert.match(controller, /\.changelog\(/);
 assert.match(read('release.gradle'), /BUILD_SOURCE_COMMIT/);
 assert.match(read('validate-device.sh'), /run_test updates-panel .*UpdatePanelTest 8/);
});
test('Settings uses installed versions and actual updater rather than placeholder', () => {
 const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/MainActivity.kt');
 assert.match(source, /UpdateSettings\(/);
 assert.doesNotMatch(source, /Browser 0\.1\.0|No release feed configured/);
});
test('Only Browser obtains network/install capability, installer callback is private', () => {
 const browser = read('browser/src/main/AndroidManifest.xml');
 assert.match(browser, /android\.permission\.INTERNET/);
 assert.match(browser, /android\.permission\.REQUEST_INSTALL_PACKAGES/);
 assert.match(browser, /UpdateResultReceiver[^>]*android:exported="false"/);
 assert.doesNotMatch(read('service/src/main/AndroidManifest.xml'), /REQUEST_INSTALL_PACKAGES/);
});
test('Release discovery propagates the operation cancellation predicate into every fetch', () => {
 const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/ReleaseClient.kt');
 assert.match(source, /fun latest\(serviceCode: Long, browserCode: Long, cancelled: \(\) -> Boolean/);
 assert.match(source, /page=\$page", true, cancelled\)/);
 assert.match(source, /fetch\(url, false, cancelled\)/);
 assert.match(read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/UpdateController.kt'), /client\.latest\([^\n]+\{ gate\.cancelled \}/);
});
test('Installer durably journals preparation before writing or linking each exact session', () => {
 const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/AndroidUpdateInstaller.kt');
 assert.match(source, /putString\("phase", "preparing"\)[\s\S]*?commit\(\)[\s\S]*?fun createRecorded/);
 assert.match(source, /setAppLabel\(marker\)/);
 assert.match(source, /val id = sessions\.createSession\(params\)[\s\S]*?putStringSet\("sessions",[\s\S]*?commit\(\)/);
 assert.match(source, /val id = createRecorded\([\s\S]*?sessions\.openSession\(id\)/);
 assert.match(source, /sessions\.mySessions \+ ids\.filter/);
 assert.match(source, /mapNotNull \{ sessions\.getSessionInfo\(it\) \}/);
 assert.match(source, /parent\.removeChildSessionId\(it\)/);
 assert.match(source, /it\.installerPackageName == context\.packageName/);
 assert.match(source, /it\.sessionId in ids \|\| marker != null && it\.appLabel\?\.toString\(\) == marker/);
});
test('Installer distinguishes durable preparation from the OS commit boundary on restart', () => {
 const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/AndroidUpdateInstaller.kt');
 assert.match(source, /putString\("phase", "prepared"\)\.commit\(\)/);
 assert.match(source, /putString\("phase", "committing"\)\.commit\(\)[\s\S]*?it\.commit\(result\.intentSender\)/);
 assert.match(source, /val keep = UpdateRecovery\.keep\(/);
 assert.match(source, /info\?\.isSealed == true/);
 assert.match(source, /if \(!keep\) cancel\(\)/);
});
test('Installer failure callback retains cleanup evidence instead of crashing the Browser', () => {
 const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/AndroidUpdateInstaller.kt');
 assert.match(source, /val cleaned = runCatching \{ installer\.cancel\(\) \}\.isSuccess/);
 assert.match(source, /putInt\("status", status\)\.remove\("confirmation"\)/);
 assert.match(source, /putString\("statusMessage", intent\.getStringExtra\(PackageInstaller\.EXTRA_STATUS_MESSAGE\)\)/);
});
test('Every Android uses standalone sessions and explicit per-product actions', () => {
 const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/AndroidUpdateInstaller.kt');
 assert.doesNotMatch(source, /setMultiPackage\(|addChildSessionId\(/);
 const ui = read('browser/src/main/java/com/everyonesoftware/storage/browser/updates/UpdateSettings.kt');
 assert.match(ui, /Install Service update/);
 assert.match(ui, /Install Browser update/);
});
test('Updater instrumentation is part of exact-count device validation', () => {
 assert.match(read('validate-device.sh'), /run_test updates-installer .*UpdateInstallerTest 4/);
 assert.match(read('validate-device.sh'), /run_test updates-settings .*UpdateSettingsTest 4/);
 assert.match(read('validate-device.sh'), /run_test updates-restart .*UpdateRestartTest 5/);
 assert.match(read('validate-device.sh'), /run_test updates-continuity-seed .*UpdateContinuityTest 1 -e phase seed/);
 assert.match(read('validate-device.sh'), /run_test updates-continuity-verify .*UpdateContinuityTest 1 -e phase verify/);
});
