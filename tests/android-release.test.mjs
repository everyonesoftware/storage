import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const root = resolve(import.meta.dirname, '..');
const scratch = process.env.TMPDIR || tmpdir();

test('portable Android native helper builds only two pinned Android targets', () => {
  assert.ok(existsSync(join(root, 'scripts/build-android-native.sh')), 'Android-only native helper is implemented');
  const fixture = mkdtempSync(join(scratch, 'storage-native-helper-test-'));
  try {
    const bin = join(fixture, 'bin');
    const ndk = join(fixture, 'sdk/ndk/29.0.14206865');
    const ndkBin = join(ndk, 'toolchains/llvm/prebuilt/linux-x86_64/bin');
    mkdirSync(bin, { recursive: true }); mkdirSync(ndkBin, { recursive: true });
    writeFileSync(join(ndk, 'source.properties'), 'Pkg.Revision = 29.0.14206865\n');
    for (const tool of ['aarch64-linux-android26-clang', 'x86_64-linux-android26-clang', 'llvm-ar']) writeFileSync(join(ndkBin, tool), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
    writeFileSync(join(bin, 'cargo'), '#!/bin/bash\nprintf "%s\\n" "$*" >> "$HELPER_TEST_LOG"\ntarget="${@: -1}"\nmkdir -p "$CARGO_TARGET_DIR/$target/release"\nprintf "test-only native fixture" > "$CARGO_TARGET_DIR/$target/release/libstorage_core.so"\n', { mode: 0o755 });
    const result = spawnSync('bash', [join(root, 'scripts/build-android-native.sh')], { encoding: 'utf8', env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, ANDROID_NDK_HOME: '', ANDROID_HOME: join(fixture, 'sdk'), CARGO_TARGET_DIR: join(fixture, 'cargo'), STORAGE_ANDROID_NATIVE_OUTPUT: join(fixture, 'out'), HELPER_TEST_LOG: join(fixture, 'calls'), LLVM_MINGW_HOME: '' } });
    assert.equal(result.status, 0, result.stderr);
    const commands = readFileSync(join(fixture, 'calls'), 'utf8').trim().split('\n');
    assert.deepEqual(commands, ['build --locked -p storage-core --release --target aarch64-linux-android', 'build --locked -p storage-core --release --target x86_64-linux-android']);
    for (const abi of ['arm64-v8a', 'x86_64']) assert.equal(readFileSync(join(fixture, 'out', abi, 'libstorage_core.so'), 'utf8'), 'test-only native fixture');
    writeFileSync(join(ndk, 'source.properties'), 'Pkg.Revision = 29x0x14206865\n');
    const wrongNdk = spawnSync('bash', [join(root, 'scripts/build-android-native.sh')], { encoding: 'utf8', env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, ANDROID_NDK_HOME: ndk, CARGO_TARGET_DIR: join(fixture, 'cargo'), STORAGE_ANDROID_NATIVE_OUTPUT: join(fixture, 'out'), HELPER_TEST_LOG: join(fixture, 'calls') } });
    assert.notEqual(wrongNdk.status, 0, 'NDK version must match literally, not as an unescaped regex');
    assert.match(wrongNdk.stderr, /NDK 29\.0\.14206865 required/);
  } finally { rmSync(fixture, { recursive: true, force: true }); }
});

test('Gradle release without credentials fails closed before execution', { skip: process.env.STORAGE_ANDROID_GRADLE_TESTS !== '1' }, () => {
  const env = { ...process.env };
  for (const key of ['STORAGE_KEYSTORE_PATH', 'STORAGE_KEYSTORE_PASSWORD', 'STORAGE_KEY_ALIAS', 'STORAGE_KEY_PASSWORD']) delete env[key];
  const result = spawnSync('bash', ['gradlew', ':service:assembleRelease', ':browser:assembleRelease', '-PstorageCommitCount=7', '--dry-run', '--offline', '--console=plain', '--max-workers=2'], { cwd: join(root, 'apps/android'), env, encoding: 'utf8', timeout: 180000 });
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stdout + result.stderr, /release signing requires all four STORAGE_KEYSTORE/);
});

test('Gradle rejects partial signing, missing keystore, absent count and invalid counts', { skip: process.env.STORAGE_ANDROID_GRADLE_TESTS !== '1' }, () => {
  const fixture = mkdtempSync(join(scratch, 'storage-gradle-config-test-'));
  try {
    const key = join(fixture, 'not-a-key-configuration-fixture');
    writeFileSync(key, 'Configuration-only fixture; dry-run never signs');
    const base = { ...process.env };
    for (const name of ['STORAGE_KEYSTORE_PATH', 'STORAGE_KEYSTORE_PASSWORD', 'STORAGE_KEY_ALIAS', 'STORAGE_KEY_PASSWORD']) delete base[name];
    const complete = { STORAGE_KEYSTORE_PATH: key, STORAGE_KEYSTORE_PASSWORD: 'not-a-secret-test', STORAGE_KEY_ALIAS: 'not-a-key-test', STORAGE_KEY_PASSWORD: 'not-a-secret-test' };
    const cases = [
      { vars: { STORAGE_KEY_ALIAS: 'partial-test' }, args: ['-PstorageCommitCount=7'], expected: /release signing requires all four/ },
      { vars: { ...complete, STORAGE_KEYSTORE_PATH: join(fixture, 'absent') }, args: ['-PstorageCommitCount=7'], expected: /keystore file does not exist/ },
      { vars: complete, args: [], expected: /release requires explicit -PstorageCommitCount/ },
      ...['0', '-1', '01', '65535', '999999999999999', 'seven'].map(value => ({ vars: complete, args: [`-PstorageCommitCount=${value}`], expected: /storageCommitCount must be an integer in 1\.\.65534/ })),
      { vars: complete, args: ['-PstorageCommitCount=7', '-PallowMissingNative=true'], expected: /release cannot use allowMissingNative/ },
    ];
    for (const scenario of cases) {
      const result = spawnSync('bash', ['gradlew', ':service:assembleRelease', ':browser:assembleRelease', ...scenario.args, '--dry-run', '--offline', '--console=plain', '--max-workers=2'], { cwd: join(root, 'apps/android'), env: { ...base, ...scenario.vars }, encoding: 'utf8', timeout: 180000 });
      assert.notEqual(result.status, 0, `Unexpected success: ${scenario.args.join(' ')}`);
      assert.match(result.stdout + result.stderr, scenario.expected);
    }
  } finally { rmSync(fixture, { recursive: true, force: true }); }
});

test('Android packager verifies both release identities before copying exact names', async () => {
  assert.ok(existsSync(join(root, 'scripts/package-android.mjs')), 'Android release packager is implemented');
  const { packageAndroid } = await import('../scripts/package-android.mjs');
  const fixture = mkdtempSync(join(scratch, 'storage-android-release-test-'));
  try {
    for (const app of ['service', 'browser']) {
      const path = join(fixture, `apps/android/${app}/build/outputs/apk/release`);
      mkdirSync(path, { recursive: true });
      writeFileSync(join(path, `${app}-release.apk`), `unit-test fixture ${app}, not an APK`);
    }
    const commands = [];
    const run = (tool, args) => {
      commands.push([tool, ...args]);
      const app = args.at(-1).includes('/service/') ? 'service' : 'browser';
      if (tool === 'signer') return `Signer #1 certificate DN: CN=Storage release test\nSigner #1 certificate SHA-256 digest: ${'a'.repeat(64)}\n`;
      if (args[0] === 'files') return app === 'service' ? ['/lib/arm64-v8a/libstorage_core.so', '/lib/x86_64/libstorage_core.so', '/lib/arm64-v8a/libstorage_descriptor_io.so', '/lib/x86_64/libstorage_descriptor_io.so'].join('\n') : '/classes.dex\n/lib/arm64-v8a/libandroidx.graphics.path.so';
      return { 'application-id': `com.everyonesoftware.storage.${app}`, 'version-name': '0.1.7', 'version-code': '7' }[args[1]];
    };
    const directory = join(fixture, 'dist');
    const files = await packageAndroid({ root: fixture, version: '0.1.7', directory, run, tools: { apksigner: 'signer', apkanalyzer: 'analyzer' } });
    assert.deepEqual(files.map(p => p.split('/').at(-1)).sort(), ['Storage-Browser-0.1.7.apk', 'Storage-Service-0.1.7.apk']);
    assert.match(readFileSync(files[0], 'utf8'), /unit-test fixture/);
    assert.ok(commands.some(c => c[0] === 'signer' && c.includes('--print-certs')));
    const negatives = [
      ['mismatched-signers', (tool, args, text) => tool === 'signer' && args.at(-1).includes('/browser/') ? text.replace('a'.repeat(64), 'b'.repeat(64)) : text, /signers differ/],
      ['debug-signer', (tool, args, text) => tool === 'signer' ? text.replace('CN=Storage release test', 'CN=Android Debug') : text, /non-debug release signer/],
      ['wrong-version', (tool, args, text) => args[1] === 'version-name' ? '0.1.8' : text, /wrong APK release version/],
      ['wrong-package', (tool, args, text) => args[1] === 'application-id' ? 'unrelated.application' : text, /wrong application ID/],
      ['missing-native', (tool, args, text) => args[0] === 'files' && args.at(-1).includes('/service/') ? text.split('\n').slice(1).join('\n') : text, /native ABI inventory/],
      ['browser-native-leak', (tool, args, text) => args[0] === 'files' && args.at(-1).includes('/browser/') ? text + '\n/lib/arm64-v8a/libstorage_core.so' : text, /native ABI inventory/],
    ];
    for (const [name, alter, expected] of negatives) {
      const rejectedDirectory = join(fixture, name);
      await assert.rejects(packageAndroid({ root: fixture, version: '0.1.7', directory: rejectedDirectory, run: (tool, args) => alter(tool, args, run(tool, args)), tools: { apksigner: 'signer', apkanalyzer: 'analyzer' } }), expected);
      assert.equal(existsSync(rejectedDirectory), false, 'Invalid pair must not create publishable-looking output');
    }
  } finally { rmSync(fixture, { recursive: true, force: true }); }
});
