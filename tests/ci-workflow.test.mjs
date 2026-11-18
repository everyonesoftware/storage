import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const workflowPath = new URL('../.github/workflows/ci.yml', import.meta.url);
const workflow = () => fs.readFileSync(workflowPath, 'utf8').replaceAll('\r\n', '\n');
const job = (text, name) => text.match(new RegExp(`^  ${name}:\\n([\\s\\S]*?)(?=^  [a-z][a-z-]*:\\n|$(?![\\s\\S]))`, 'm'))?.[1] ?? '';

test('one main workflow owns selection, one native build per platform, and shared publication', () => {
 const text = workflow();
 const jobs = text.split('\njobs:\n')[1];
 assert.ok(jobs, 'Missing jobs mapping');
 assert.deepEqual([...jobs.matchAll(/^  ([a-z][a-z-]*):\n/gm)].map(m => m[1]), ['validate', 'android', 'windows', 'release']);
 assert.match(text, /push:\n\s+branches: \[main\]/);
 assert.match(text, /pull_request:/);
 assert.match(text, /workflow_dispatch:/);
 assert.match(job(text, 'validate'), /release\.mjs select/);
 for (const platform of ['android', 'windows']) {
  assert.match(job(text, platform), /needs: validate/);
  assert.match(job(text, platform), new RegExp(`needs\\.validate\\.outputs\\.${platform} == 'true'`));
 }
 assert.doesNotMatch(text, /cancel-in-progress: true|paths-ignore:|pull_request_target|\.py\b|python|--clobber/i);
});

test('only serialized release job can publish, and failed builds do not become assets', () => {
 const text = workflow();
 assert.equal([...text.matchAll(/contents: write/g)].length, 1);
 const publish = job(text, 'release');
 assert.match(publish, /contents: write/);
 assert.match(publish, /cancel-in-progress: false/);
 assert.match(publish, /github\.ref == 'refs\/heads\/main'/);
 assert.match(publish, /github\.event_name != 'pull_request'/);
 assert.match(publish, /needs\.android\.result == 'success'/);
 assert.match(publish, /needs\.windows\.result == 'success'/);
 assert.match(publish, /release\.mjs publish/);
 assert.doesNotMatch(publish, /--clobber|gh release delete|cargo build|gradlew|dotnet publish/);
});

test('release Android signing is gated away from PRs and secrets have no debug fallback', () => {
 const android = job(workflow(), 'android');
 assert.match(android, /github\.ref == 'refs\/heads\/main' && github\.event_name != 'pull_request'/);
 for (const secret of ['STORAGE_KEYSTORE_BASE64', 'STORAGE_KEYSTORE_PASSWORD', 'STORAGE_KEY_ALIAS', 'STORAGE_KEY_PASSWORD']) assert.match(android, new RegExp(`secrets\\.${secret}`));
 assert.match(android, /Missing repository secret/);
 assert.match(android, /umask 077/);
 assert.match(android, /trap .*EXIT/);
 assert.match(android, /package-android\.mjs/);
 assert.match(android, /build-android-native\.sh/);
 assert.match(android, /validate-device\.sh emulator-5554/);
 assert.doesNotMatch(android, /allowMissingNative|assembleDebug.*dist\/android|debug\.keystore/);
});

test('Windows publishes only after native installer lifecycle, with diagnostics retained', () => {
 const windows = job(workflow(), 'windows');
 assert.match(windows, /runs-on: windows-2025/);
 assert.match(windows, /x86_64-pc-windows-msvc/);
 assert.match(windows, /build-windows-installer\.ps1/);
 assert.match(windows, /test-windows-installer\.ps1/);
 assert.match(windows, /timeout-minutes: 5/);
 assert.match(windows, /if: always\(\)/);
 assert.match(windows, /release\.mjs manifest --platform windows/);
 assert.doesNotMatch(windows, /OutputType=Library|PrepareForRunDependsOn|AppxGeneratePriEnabled=false/);
 assert.ok(windows.indexOf('test-windows-installer.ps1') < windows.indexOf('name: Retain verified Windows release'));
});

test('Android installs both Rust targets explicitly before cross compilation', () => {
 const android = job(workflow(), 'android');
 const install = android.indexOf('rustup target add --toolchain 1.100.0 aarch64-linux-android x86_64-linux-android');
 assert.ok(install >= 0, 'Fresh runners cannot rely on rust-toolchain.toml when RUSTUP_TOOLCHAIN overrides it');
 assert.ok(install < android.indexOf('bash scripts/build-android-native.sh'));
});

test('Android overrides hosted-runner NDK defaults and exercises real signing refusals', () => {
 const android = job(workflow(), 'android');
 assert.ok(android.includes('ANDROID_NDK_HOME=$ANDROID_HOME/ndk/29.0.14206865'));
 assert.ok(android.includes("STORAGE_ANDROID_GRADLE_TESTS: '1'"));
 assert.ok(android.indexOf('name: Verify release signing configuration fails closed') > android.indexOf(':denied:assembleDebugAndroidTest'));
});

test('diagnostic and release artifacts support rerunning the same workflow', () => {
 const uploads = workflow().split(/uses: actions\/upload-artifact@[a-f0-9]{40}/).slice(1);
 assert.equal(uploads.length, 4);
 for (const upload of uploads) assert.match(upload.split(/\n      - name:|\n  [a-z]+:/)[0], /overwrite: true/);
});

test('documented queue:max lint exception rejects every other queue layout', async () => {
 const { validateQueueSyntax } = await import('../scripts/lint-workflow.mjs');
 const text = workflow();
 assert.doesNotThrow(() => validateQueueSyntax(text));
 assert.throws(() => validateQueueSyntax(text.replace('queue: max', 'queue: anything')));
 assert.throws(() => validateQueueSyntax(text.replace('queue: max', 'queue: max\n      queue: single')));
 assert.throws(() => validateQueueSyntax(text.replace('queue: max', 'queue: max\n      "queue": single')));
 assert.throws(() => validateQueueSyntax(text.replace('cancel-in-progress: false', 'cancel-in-progress: true')));
});

test('all third-party actions are immutable commit pins and version derives from full history', () => {
 const text = workflow();
 const actions = [...text.matchAll(/uses: ([^\s]+)/g)].map(m => m[1]);
 assert.ok(actions.length >= 8);
 for (const action of actions) assert.match(action, /^[\w.-]+\/[\w./-]+@[a-f0-9]{40}$/);
 assert.match(text, /fetch-depth: 0/);
 assert.match(text, /persist-credentials: false/);
 assert.match(text, /release\.mjs version/);
});
