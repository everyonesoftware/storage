import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const read = path => readFileSync(new URL(`../apps/android/${path}`, import.meta.url), 'utf8');
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
