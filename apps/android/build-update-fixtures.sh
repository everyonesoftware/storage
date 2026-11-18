#!/usr/bin/env bash
# Disposable DEBUG-signed APKs for instrumentation, not release/publication artifacts.
# Reuses Gradle's existing development identity, never reads a production signing key.
set -euo pipefail
cd "$(dirname "$0")"
args=(--console=plain --max-workers=2)
bash gradlew :service:assembleDebug :browser:assembleDebug -PstorageCommitCount=2 "${args[@]}"
mkdir -p browser/build/update-fixtures/updates
cp service/build/outputs/apk/debug/service-debug.apk browser/build/update-fixtures/updates/Service.apk
cp browser/build/outputs/apk/debug/browser-debug.apk browser/build/update-fixtures/updates/Browser.apk
# Restore code-1 product outputs before assembling tests; fixtures are never installed implicitly.
bash gradlew :service:assembleDebug :browser:assembleDebug :service:assembleDebugAndroidTest \
 :browser:assembleDebugAndroidTest :denied:assembleDebug :denied:assembleDebugAndroidTest "${args[@]}"
