import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

const root = process.cwd();
const [sourceFixture, exportedFixture] = process.argv.slice(2);
assert.ok(sourceFixture && exportedFixture, 'Usage: node scripts/collect-validation.mjs <GUI-imported-source> <GUI-exported-file>');
const resolve = relative => path.isAbsolute(relative) ? relative : path.join(root, relative);
const read = relative => fs.readFileSync(resolve(relative), 'utf8');
const core = read('artifacts/validation/core-tests.log');
const coreResults = [...core.matchAll(/test result: ok\. (\d+) passed; (\d+) failed; (\d+) ignored/g)];
assert.ok(coreResults.length >= 2, 'Missing actual Rust test summaries');
assert.ok(coreResults.every(m => m[2] === '0' && m[3] === '0'), 'Rust failure or ignored test');
const rustPassed = coreResults.reduce((n, m) => n + Number(m[1]), 0);
assert.equal(rustPassed, 19);
const abi = read('artifacts/validation/native-abi.log');
assert.match(abi, /pass 5\b/);
assert.match(abi, /fail 0\b/);
assert.match(abi, /skipped 0\b/);
const windows = read('apps/windows/validation/logic-transport.txt');
assert.doesNotMatch(windows, /SKIP|FAIL/);
const windowsAssertions = windows.split(/\r?\n/).filter(x => x.startsWith('PASS ')).length;
assert.equal(windowsAssertions, 43);
const androidClasses = [
  ['service-binder', 8], ['denied-caller', 1], ['cold-seed', 1],
  ['cold-verify', 1], ['cold-binding', 1], ['listing-notice', 2], ['browser-binder', 2],
];
const android = androidClasses.map(([name, expected]) => {
  const text = read(`apps/android/build/device-validation/${name}.log`);
  const count = Number(text.match(/OK \((\d+) tests?\)/)?.[1]);
  assert.equal(count, expected, `Unexpected runtime total in ${name}`);
  assert.doesNotMatch(text, /FAILURES!!!|shortMsg=|INSTRUMENTATION_FAILED|INSTRUMENTATION_STATUS_CODE:\s*-\d+/);
  return { name, passed: count };
});
const jvmDir = 'apps/android/service/build/test-results/testDebugUnitTest';
const jvmFiles = fs.readdirSync(path.join(root, jvmDir)).filter(x => /^TEST-.*\.xml$/.test(x));
const jvm = jvmFiles.reduce((total, name) => {
  const text = read(`${jvmDir}/${name}`);
  const attribute = key => Number(text.match(new RegExp(`\\b${key}="(\\d+)"`))?.[1]);
  assert.equal(attribute('failures'), 0);
  assert.equal(attribute('errors'), 0);
  assert.equal(attribute('skipped'), 0);
  return total + attribute('tests');
}, 0);
assert.equal(jvm, 5);
const digest = relative => crypto.createHash('sha256').update(fs.readFileSync(resolve(relative))).digest('hex');
const products = [
  ['Storage-Service-0.1.0-dev.apk', 'apps/android/service/build/outputs/apk/debug/service-debug.apk'],
  ['Storage-Browser-0.1.0-dev.apk', 'apps/android/browser/build/outputs/apk/debug/browser-debug.apk'],
].map(([name, source]) => {
  const relative = `artifacts/deliverables/${name}`;
  assert.equal(digest(relative), digest(source), 'Delivered APK differs from tested build');
  return { name, bytes: fs.statSync(relative).size, sha256: digest(relative), signing: 'Android development debug certificate; not a production release' };
});
const manifest = {
  generatedAt: new Date().toISOString(),
  stage: 'local development slice; distributed release incomplete',
  verification: {
    rustTests: rustPassed, independentCAbiTests: 5, windowsTransportAssertions: windowsAssertions,
    androidJvmTests: jvm, androidInstrumentationExecutions: android.reduce((n, x) => n + x.passed, 0), android,
    windowsNativeGui: 'not run; Windows execution target required',
    physicalAndroidPhone: 'not run; owned API29 x86_64 emulator only',
    graphicalPickerFileComparison: { byteIdentical: digest(sourceFixture) === digest(exportedFixture), bytes: fs.statSync(resolve(sourceFixture)).size, sha256: digest(sourceFixture) },
  },
  products,
  native: ['linux-x64/libstorage_core.so', 'windows-x64/storage_core.dll', 'android/arm64-v8a/libstorage_core.so', 'android/x86_64/libstorage_core.so'].map(name => ({ name, sha256: digest(`artifacts/native/${name}`) })),
};
assert.equal(digest(sourceFixture), digest(exportedFixture));
fs.writeFileSync('artifacts/deliverables/validation-manifest.json', JSON.stringify(manifest, null, 2) + '\n');
console.log(JSON.stringify(manifest, null, 2));
