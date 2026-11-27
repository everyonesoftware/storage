#!/usr/bin/env bash
# Explicitly owned emulator/device only. Does not create or stop emulators.
set -euo pipefail
cd "$(dirname "$0")"
serial=${1:?Usage: bash validate-device.sh <task-owned-serial>}
out="build/device-validation"
mkdir -p "$out"
adb -s "$serial" get-state
for apk in service/build/outputs/apk/debug/service-debug.apk browser/build/outputs/apk/debug/browser-debug.apk denied/build/outputs/apk/debug/denied-debug.apk service/build/outputs/apk/androidTest/debug/service-debug-androidTest.apk browser/build/outputs/apk/androidTest/debug/browser-debug-androidTest.apk denied/build/outputs/apk/androidTest/debug/denied-debug-androidTest.apk; do
 adb -s "$serial" install -r "$apk"
done
run_test() {
 local label=$1 package=$2 class=$3 expected=$4
 shift 4
 adb -s "$serial" shell am instrument -w -r -e class "$class" "$@" "$package.test/androidx.test.runner.AndroidJUnitRunner" | tee "$out/$label.log"
 node - "$out/$label.log" "$expected" <<'JS'
const fs = require('node:fs');
const text = fs.readFileSync(process.argv[2], 'utf8');
const match = text.match(/OK \((\d+) tests?\)/);
if (!match || Number(match[1]) !== Number(process.argv[3]) || /FAILURES!!!|shortMsg=|INSTRUMENTATION_FAILED|INSTRUMENTATION_STATUS_CODE:\s*-\d+/.test(text)) {
 console.error('Instrumentation did not complete the exact expected passing tests'); process.exit(1);
}
JS
}
run_test updates-installer com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.updates.UpdateInstallerTest 4
run_test updates-settings com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.updates.UpdateSettingsTest 4
run_test updates-panel com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.updates.UpdatePanelTest 8
# API 35 can kill the UID when REQUEST_INSTALL_PACKAGES is revoked. Keep that
# transition outside instrumentation, including failure cleanup. Never approve installs.
run_restart_tests() (
 local package=com.everyonesoftware.storage.browser before mode
 before=$(adb -s "$serial" shell appops get "$package" REQUEST_INSTALL_PACKAGES)
 mode=$(printf '%s\n' "$before" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>console.log(s.match(/REQUEST_INSTALL_PACKAGES: ([a-z]+)/)?.[1] ?? "default"))')
 trap 'adb -s "$serial" shell appops set "$package" REQUEST_INSTALL_PACKAGES "$mode"' EXIT
 adb -s "$serial" shell appops set "$package" REQUEST_INSTALL_PACKAGES allow
 run_test updates-restart "$package" com.everyonesoftware.storage.browser.updates.UpdateRestartTest 5
)
run_restart_tests
run_test service-binder com.everyonesoftware.storage.service com.everyonesoftware.storage.service.StorageBinderTest 8
adb -s "$serial" shell pm revoke com.everyonesoftware.storage.service android.permission.CAMERA
# The fresh API 29 CI emulator has no prior permission decision and does not
# implement clear-permission-flags. On supported systems, reset reused test data.
# The API 29 help handler prints a valid command list but returns -1 (255 via adb).
help_status=0
package_help=$(adb -s "$serial" shell cmd package help) || help_status=$?
if (( help_status != 0 && help_status != 255 )) || [[ "$package_help" != 'Package manager (package) commands:'* ]]; then
 printf 'Unable to inspect package manager commands (status %s): %s\n' "$help_status" "$package_help" >&2
 exit "$((help_status == 0 ? 1 : help_status))"
fi
if [[ "$package_help" == *clear-permission-flags* ]]; then
 adb -s "$serial" shell pm clear-permission-flags com.everyonesoftware.storage.service android.permission.CAMERA user-set user-fixed
fi
run_test service-pairing com.everyonesoftware.storage.service com.everyonesoftware.storage.service.PairingActivityTest 7
run_test denied-caller com.everyonesoftware.storage.denied com.everyonesoftware.storage.denied.DeniedCallerTest 1
adb -s "$serial" shell am force-stop com.everyonesoftware.storage.browser
run_test cold-seed com.everyonesoftware.storage.service com.everyonesoftware.storage.service.ColdRestartTest 1 -e phase seed
adb -s "$serial" shell am force-stop com.everyonesoftware.storage.service
run_test cold-verify com.everyonesoftware.storage.service com.everyonesoftware.storage.service.ColdRestartTest 1 -e phase verify
run_test cold-binding com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.ColdBindingRestartTest 1
run_test updates-continuity-seed com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.updates.UpdateContinuityTest 1 -e phase seed
adb -s "$serial" shell am force-stop com.everyonesoftware.storage.service
run_test updates-continuity-verify com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.updates.UpdateContinuityTest 1 -e phase verify
run_test listing-notice com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.ListingNoticeTest 2
run_test browser-binder com.everyonesoftware.storage.browser com.everyonesoftware.storage.browser.BrowserBinderTest 2
adb -s "$serial" pull /sdcard/Android/data/com.everyonesoftware.storage.browser/files/items-grid.png "$out/items-grid.png"
adb -s "$serial" pull /sdcard/Android/data/com.everyonesoftware.storage.browser/files/item-details.png "$out/item-details.png"
printf 'Device validation passed; logs and screenshots: %s\n' "$out"
