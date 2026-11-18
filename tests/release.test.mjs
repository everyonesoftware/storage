import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, symlinkSync } from 'node:fs';
import { join, resolve, basename } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';

const moduleUrl = new URL('../scripts/release.mjs', import.meta.url);
const scratch = process.env.TMPDIR || tmpdir();
function git(root, ...args) {
  const r = spawnSync('git', ['-C', root, ...args], { encoding: 'utf8', shell: false });
  assert.equal(r.status, 0, r.stderr); return r.stdout.trim();
}
function fixture(t) {
  const root = mkdtempSync(join(scratch, 'storage-release-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  git(root, 'init', '-b', 'main'); git(root, 'config', 'user.email', 'fixture@example.invalid'); git(root, 'config', 'user.name', 'Fixture');
  return root;
}
function commit(root, name = 'core/input', content = 'one') {
  mkdirSync(resolve(root, name, '..'), { recursive: true }); writeFileSync(join(root, name), content);
  git(root, 'add', '.'); git(root, 'commit', '-m', name); return git(root, 'rev-parse', 'HEAD');
}

function payloadDirectory(t, root, platform, version = '0.1.1') {
  const directory = mkdtempSync(join(scratch, `storage-${platform}-`));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const names = platform === 'android' ? [`Storage-Service-${version}.apk`, `Storage-Browser-${version}.apk`] : [`StorageWindows-${version}-win-x64-Setup.exe`, `StorageWindows-${version}-win-x64-Setup.exe.sha256`, `StorageWindows-${version}-win-x64-full.nupkg`, 'releases.win-x64.json', 'assets.win-x64.json', 'RELEASES-win-x64'];
  for (const name of names) writeFileSync(join(directory, name), `disposable test payload ${name}`);
  if (platform === 'windows') writeFileSync(join(directory, names[1]), `${hash(readFileSync(join(directory, names[0])))}  ${names[0]}\n`);
  return directory;
}
function hash(bytes) { return createHash('sha256').update(bytes).digest('hex'); }

test('manifest validates exact inventory, hashes, source and Windows installer sidecar', async t => {
  const { createManifest, validateDirectory, deriveSource } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root);
  for (const platform of ['android', 'windows']) {
    const directory = payloadDirectory(t, root, platform);
    const manifest = createManifest({ root, platform, directory });
    assert.equal(manifest.sourceCommit, source.sha); assert.equal(manifest.signing, platform === 'android' ? 'release-key' : 'unsigned');
    assert.equal(manifest.files.length, platform === 'android' ? 3 : 7);
    assert.deepEqual(validateDirectory({ root, platform, directory }).manifest, manifest);
    assert.deepEqual(createManifest({ root, platform, directory }), manifest);
    writeFileSync(join(directory, manifest.files[0].name), 'tampered');
    assert.throws(() => validateDirectory({ root, platform, directory }), /hash|checksum|metadata/);
  }
});

test('manifest refuses unexpected files, symlinks, stale source and wrong sidecar', async t => {
  const { createManifest, validateDirectory } = await import(moduleUrl);
  const root = fixture(t); commit(root);
  const directory = payloadDirectory(t, root, 'android');
  writeFileSync(join(directory, 'unexpected'), 'no');
  assert.throws(() => createManifest({ root, platform: 'android', directory }), /inventory|[Uu]nexpected/);
  rmSync(join(directory, 'unexpected')); const apk = join(directory, 'Storage-Service-0.1.1.apk'); rmSync(apk); symlinkSync(join(directory, 'Storage-Browser-0.1.1.apk'), apk);
  assert.throws(() => createManifest({ root, platform: 'android', directory }), /regular|link/);
  rmSync(apk); writeFileSync(apk, 'restored'); createManifest({ root, platform: 'android', directory });
  commit(root, 'core/changed', 'next');
  assert.throws(() => validateDirectory({ root, platform: 'android', directory }), /inventory|source|version/);
  const windows = payloadDirectory(t, root, 'windows', '0.1.2');
  writeFileSync(join(windows, 'StorageWindows-0.1.2-win-x64-Setup.exe.sha256'), `${'0'.repeat(64)}  StorageWindows-0.1.2-win-x64-Setup.exe\n`);
  assert.throws(() => createManifest({ root, platform: 'windows', directory: windows }), /sidecar/);
});
function publicationApi({ source, platform, manifest, assets = [], releases, object }) {
  const release = { id: 1, tag_name: source.tag, target_commitish: source.sha, draft: false, prerelease: false };
  return { list(path) { if (path === 'releases') return releases || [release]; if (path === 'releases/1/assets') return assets; throw new Error(`unexpected list ${path}`); },
    json(path) { if (path.startsWith('git/ref/tags/')) return { object: object || { type: 'commit', sha: source.sha } }; throw new Error(`unexpected json ${path}`); },
    manifest() { return manifest; } };
}
function manifestAssets(manifest) {
  return [...manifest.files, { name: `storage-${manifest.platform}-release.json`, bytes: Buffer.byteLength(JSON.stringify(manifest)), sha256: hash(JSON.stringify(manifest)) }].map((f, i) => ({ id: i + 1, name: f.name, size: f.bytes, state: 'uploaded', digest: `sha256:${f.sha256}` }));
}

test('selection compares per-platform verified publications, not previous push, and ignores docs', async t => {
  const { deriveSource, createManifest, publishedBaseline, decideFromBaseline, affectedPlatforms, select } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root);
  const directory = payloadDirectory(t, root, 'android'); const manifest = createManifest({ root, platform: 'android', directory });
  const api = publicationApi({ source, manifest, assets: manifestAssets(manifest) });
  assert.equal(publishedBaseline('android', { api }), source.sha);
  assert.equal(publishedBaseline('windows', { api }), null);
  commit(root, 'docs/notes.md'); assert.equal(decideFromBaseline({ root, platform: 'android', baseline: source.sha }).build, false);
  commit(root, 'apps/android/input'); commit(root, 'docs/later.md');
  assert.equal(decideFromBaseline({ root, platform: 'android', baseline: source.sha }).build, true);
  assert.equal(decideFromBaseline({ root, platform: 'windows', baseline: source.sha }).build, false);
  assert.deepEqual(affectedPlatforms(['apps/windows/input', 'docs/notes.md']), ['windows']);
  assert.deepEqual(affectedPlatforms(['.github/workflows/ci.yml']), ['android', 'windows']);
  assert.deepEqual(affectedPlatforms(['unknown-input']), ['android', 'windows']);
  assert.deepEqual(affectedPlatforms(['scripts/package-android.mjs', 'tests/android-release.test.mjs']), ['android']);
  const result = select({ root, repository: 'owner/repo', target: 'windows', api });
  assert.equal(result.android, false); assert.equal(result.android_changed, true); assert.equal(result.windows, true);
});

test('baseline fails closed on present malformed or incomplete manifests, asset digests, and wrong tags', async t => {
  const { deriveSource, createManifest, publishedBaseline } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root);
  const directory = payloadDirectory(t, root, 'android'); const manifest = createManifest({ root, platform: 'android', directory });
  for (const mutate of [
    x => { x.manifest.sourceCommit = 'a'.repeat(40); },
    x => { x.assets.pop(); x.assets.push({ id: 55, name: 'storage-android-release.json', size: 9, state: 'uploaded' }); x.assets.shift(); },
    x => { x.assets[0].digest = `sha256:${'0'.repeat(64)}`; },
    x => { x.object = { type: 'commit', sha: 'b'.repeat(40) }; },
    x => { x.assets.push({ ...x.assets[0] }); },
  ]) {
    const input = { source, manifest: structuredClone(manifest), assets: manifestAssets(manifest) }; mutate(input);
    assert.throws(() => publishedBaseline('android', { api: publicationApi(input) }), /manifest|metadata|inventory|source|digest|asset|tag/i);
  }
  assert.equal(publishedBaseline('android', { api: publicationApi({ source, manifest, assets: [] }) }), null);
});

test('selection skips source included by newer publication and rejects divergent history', async t => {
  const { decideFromBaseline } = await import(moduleUrl);
  const root = fixture(t); const old = commit(root); const newer = commit(root, 'apps/android/new');
  git(root, 'checkout', '--detach', old);
  assert.equal(decideFromBaseline({ root, platform: 'android', baseline: newer }).build, false);
  commit(root, 'apps/android/divergent');
  assert.throws(() => decideFromBaseline({ root, platform: 'android', baseline: newer }), /diverge/i);
});

test('GitHub inventory pagination is bounded, explicit and rejects API errors', async () => {
  const { githubClient } = await import(moduleUrl); let calls = 0;
  const run = (cmd, args, options) => { assert.equal(cmd, 'gh'); assert.equal(options.shell, false); calls++; assert.match(args[1], /per_page=100&page=/); return { status: 0, stdout: JSON.stringify(Array(100).fill({ id: 1 })) }; };
  assert.throws(() => githubClient('owner/repo', { run }).list('releases'), /pagination|20|limit/); assert.equal(calls, 20);
  assert.throws(() => githubClient('owner/repo', { run: () => ({ status: 1, stderr: 'HTTP 403 denied' }) }).json('releases/tags/v0.1.1', { optional: true }), /403/);
});

// Stateful command-boundary simulator only: disposable bytes, never a GitHub publication.
function fakeGitHub(source, { existing = false } = {}) {
  let release = existing ? { id: 31, tag_name: source.tag, target_commitish: source.sha, draft: false, prerelease: false, name: 'Original title', body: 'Original notes' } : null;
  let tag = existing ? source.sha : null, nextId = 100, latest = null;
  const assets = [], bytes = new Map(), writes = [], downloads = [];
  const ok = value => ({ status: 0, stdout: typeof value === 'string' ? value : JSON.stringify(value), stderr: '' });
  const missing = () => ({ status: 1, stdout: '', stderr: 'gh: Not Found (HTTP 404)' });
  const state = { assets, bytes, writes, downloads, failName: null, tamperName: null, mutateMetadata: null, extraReleases: [], tagOverride: null,
    get release() { return release; }, set release(value) { release = value; },
    seed(name, data) { const asset = { id: nextId++, name, size: data.length, state: 'uploaded', digest: `sha256:${hash(data)}`, download_count: 0, label: '' }; assets.push(asset); bytes.set(asset.id, Buffer.from(data)); return asset; },
    run(cmd, args, options) {
      if (cmd === 'git') return spawnSync(cmd, args, options);
      assert.equal(cmd, 'gh'); assert.equal(options.shell, false); assert.ok(options.timeout > 0); assert.ok(!args.includes('--clobber'));
      if (args[0] === 'api') {
        const path = args[1].replace(/^repos\/owner\/repo\//, '');
        if (args.includes('POST')) {
          const payload = JSON.parse(options.input); writes.push({ create: payload });
          release = { id: 31, ...payload }; tag = payload.target_commitish; return ok(release);
        }
        if (args.includes('PATCH')) { writes.push({ patch: JSON.parse(options.input) }); latest = release.id; return ok(release); }
        if (path.startsWith('releases?')) return ok([...(release ? [release] : []), ...state.extraReleases]);
        if (/^releases\/\d+\/assets\?/.test(path)) return ok(path.startsWith('releases/31/') ? structuredClone(assets) : []);
        if (path === 'releases/latest') return latest ? ok(release) : missing();
        if (path.startsWith('releases/tags/')) return release ? ok(release) : missing();
        if (path.startsWith('git/ref/tags/')) return (state.tagOverride || tag) ? ok({ object: { type: 'commit', sha: state.tagOverride || tag } }) : missing();
        if (path.startsWith('releases/assets/')) { const id = Number(path.split('/').at(-1)); return bytes.has(id) ? ok(bytes.get(id).toString()) : missing(); }
        throw new Error(`Unhandled API ${path}`);
      }
      if (args[0] === 'release' && args[1] === 'upload') {
        const path = args[3], name = basename(path); writes.push({ upload: name });
        if (state.failName === name) return { status: 1, stderr: 'simulated upload failure' };
        assert.ok(!assets.some(a => a.name === name), 'never replace assets'); state.seed(name, readFileSync(path)); return ok('');
      }
      if (args[0] === 'release' && args[1] === 'download') {
        const name = args[args.indexOf('--pattern') + 1], dir = args[args.indexOf('--dir') + 1];
        downloads.push(name); const asset = assets.find(a => a.name === name); assert.ok(asset);
        writeFileSync(join(dir, name), state.tamperName === name ? 'remote tampered' : bytes.get(asset.id)); asset.download_count++;
        if (state.mutateMetadata) state.mutateMetadata(assets);
        return ok('');
      }
      throw new Error(`Unhandled gh command ${args.join(' ')}`);
    },
  };
  return state;
}

test('publisher creates one shared stable release, verifies bytes and is idempotent without metadata edits', async t => {
  const { deriveSource, createManifest, publish } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root), fake = fakeGitHub(source);
  const androidDirectory = payloadDirectory(t, root, 'android'), windowsDirectory = payloadDirectory(t, root, 'windows');
  createManifest({ root, platform: 'android', directory: androidDirectory }); createManifest({ root, platform: 'windows', directory: windowsDirectory });
  const result = publish({ root, repository: 'owner/repo', androidDirectory, windowsDirectory, run: fake.run });
  assert.deepEqual(result.published, ['android', 'windows']);
  const uploads = fake.writes.filter(w => w.upload).map(w => w.upload);
  assert.equal(uploads.length, 12); assert.equal(uploads[3], 'storage-android-release.json'); assert.equal(uploads.at(-1), 'storage-windows-release.json');
  const created = fake.writes.find(w => w.create).create;
  assert.equal(created.make_latest, 'false'); assert.equal(created.draft, false); assert.equal(created.prerelease, false);
  assert.match(created.body, /unsigned/i); assert.match(created.body, /local|pairing|replication/i);
  const writeCount = fake.writes.length;
  publish({ root, repository: 'owner/repo', androidDirectory, windowsDirectory, run: fake.run });
  assert.equal(fake.writes.length, writeCount); assert.ok(fake.downloads.length >= 24);
});

test('publisher repairs only missing assets and never overwrites conflicts', async t => {
  const { deriveSource, createManifest, publish } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root), fake = fakeGitHub(source, { existing: true });
  const androidDirectory = payloadDirectory(t, root, 'android'); createManifest({ root, platform: 'android', directory: androidDirectory });
  fake.seed('unrelated.txt', Buffer.from('preserved'));
  fake.seed('Storage-Service-0.1.1.apk', readFileSync(join(androidDirectory, 'Storage-Service-0.1.1.apk')));
  fake.failName = 'Storage-Browser-0.1.1.apk';
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: fake.run }), /failure/);
  assert.ok(!fake.assets.some(a => a.name === 'storage-android-release.json'));
  fake.failName = null;
  publish({ root, repository: 'owner/repo', androidDirectory, run: fake.run });
  assert.equal(fake.release.name, 'Original title'); assert.equal(fake.release.body, 'Original notes'); assert.ok(!fake.writes.some(w => w.patch || w.create));
  assert.ok(!fake.writes.some(w => w.upload === 'Storage-Service-0.1.1.apk'));
  const conflict = fakeGitHub(source, { existing: true }); conflict.seed('Storage-Service-0.1.1.apk', Buffer.from('conflict'));
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: conflict.run }), /digest|hash|size|conflict/i);
  assert.equal(conflict.writes.length, 0);
});

test('publisher rejects readback tampering, existing metadata mutation, wrong-source releases and dirty source', async t => {
  const { deriveSource, createManifest, publish } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root);
  const androidDirectory = payloadDirectory(t, root, 'android'); createManifest({ root, platform: 'android', directory: androidDirectory });
  const tampered = fakeGitHub(source); tampered.tamperName = 'Storage-Service-0.1.1.apk';
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: tampered.run }), /hash|digest|size/i);
  assert.ok(!tampered.assets.some(a => a.name === 'storage-android-release.json'));
  const metadata = fakeGitHub(source, { existing: true }); metadata.seed('unrelated.txt', Buffer.from('preserve'));
  metadata.mutateMetadata = assets => { assets[0].label = 'changed'; };
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: metadata.run }), /preserv|metadata|changed/i);
  for (const mutation of [{ draft: true }, { prerelease: true }, { target_commitish: 'a'.repeat(40) }]) {
    const wrong = fakeGitHub(source, { existing: true }); Object.assign(wrong.release, mutation);
    assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: wrong.run }), /source|draft|stable|release/i); assert.equal(wrong.writes.length, 0);
  }
  const wrongTag = fakeGitHub(source, { existing: true }); wrongTag.tagOverride = 'b'.repeat(40);
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: wrongTag.run }), /source|tag/i); assert.equal(wrongTag.writes.length, 0);
  writeFileSync(join(root, 'dirty'), 'no');
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: fakeGitHub(source).run }), /clean/i);
});

test('publisher has no writes without successful platform artifacts', async t => {
  const { publish } = await import(moduleUrl); const root = fixture(t); commit(root);
  const result = publish({ root, repository: 'owner/repo', run: () => { throw new Error('must not call commands'); } });
  assert.deepEqual(result.published, []);
});

test('CLI exposes manifest, select GitHub outputs, publish no-op and rejects unsupported options', async t => {
  const { main } = await import(moduleUrl); const root = fixture(t); commit(root);
  const directory = payloadDirectory(t, root, 'android'), output = join(directory, '..', `release-output-${process.pid}`);
  t.after(() => rmSync(output, { force: true }));
  const manifest = main(['manifest', '--platform', 'android', '--directory', directory], { root, output: () => {} });
  assert.equal(manifest.platform, 'android');
  const run = (cmd, args, opts) => cmd === 'git' ? spawnSync(cmd, args, opts) : { status: 0, stdout: '[]' };
  const selection = main(['select', '--repository', 'owner/repo', '--target', 'windows', '--github-output', output], { root, run, output: () => {} });
  assert.equal(selection.windows, true); assert.equal(selection.android, false);
  assert.match(readFileSync(output, 'utf8'), /android=false\nwindows=true\nandroid_changed=true\nwindows_changed=true\nreasons=\{/);
  assert.deepEqual(main(['publish', '--repository', 'owner/repo'], { root, output: () => {} }).published, []);
  for (const argv of [['version', '--repository', 'owner/repo'], ['manifest', '--platform', 'android'], ['select', '--repository', '../repo'], ['select', '--repository', 'owner/repo', '--target', 'auto', '--target', 'all'], ['publish', '--repository', 'owner/repo', '--directory', directory]]) assert.throws(() => main(argv, { root, run, output: () => {} }), /option|directory|repository|[Uu]sage|[Ii]nvalid/);
});

test('publisher lets successful Windows complete after Android upload failure without advancing Android baseline', async t => {
  const { deriveSource, createManifest, publish, publishedBaseline, githubClient } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root), fake = fakeGitHub(source);
  const androidDirectory = payloadDirectory(t, root, 'android'), windowsDirectory = payloadDirectory(t, root, 'windows');
  createManifest({ root, platform: 'android', directory: androidDirectory }); createManifest({ root, platform: 'windows', directory: windowsDirectory });
  fake.failName = 'Storage-Service-0.1.1.apk';
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, windowsDirectory, run: fake.run }), /verified platforms: windows/);
  const api = githubClient('owner/repo', { run: fake.run });
  assert.equal(publishedBaseline('android', { api }), null); assert.equal(publishedBaseline('windows', { api }), source.sha);
});

test('publisher preflights every existing platform asset before any new upload', async t => {
  const { deriveSource, createManifest, publish } = await import(moduleUrl);
  const root = fixture(t); commit(root); const fake = fakeGitHub(deriveSource(root), { existing: true });
  const androidDirectory = payloadDirectory(t, root, 'android'); createManifest({ root, platform: 'android', directory: androidDirectory });
  fake.seed('Storage-android-SHA256SUMS', Buffer.from('conflicting checksum'));
  assert.throws(() => publish({ root, repository: 'owner/repo', androidDirectory, run: fake.run }), /digest|size|hash/);
  assert.equal(fake.writes.length, 0);
});

test('publisher does not promote an older new release above a numerically newer stable release', async t => {
  const { deriveSource, createManifest, publish } = await import(moduleUrl);
  const root = fixture(t); commit(root); const fake = fakeGitHub(deriveSource(root));
  fake.extraReleases.push({ id: 99, tag_name: 'v0.1.10', target_commitish: 'a'.repeat(40), draft: false, prerelease: false });
  const androidDirectory = payloadDirectory(t, root, 'android'); createManifest({ root, platform: 'android', directory: androidDirectory });
  publish({ root, repository: 'owner/repo', androidDirectory, run: fake.run });
  assert.ok(!fake.writes.some(w => w.patch));
});

test('annotated tags are dereferenced with cycle and chain bounds; API manifest bytes are verified', async () => {
  const { resolveTag, githubClient } = await import(moduleUrl);
  const sha = 'a'.repeat(40), tagSha = 'b'.repeat(40);
  assert.equal(resolveTag({ json: path => path.startsWith('git/ref/') ? { object: { type: 'tag', sha: tagSha } } : { object: { type: 'commit', sha } } }, 'v0.1.1'), sha);
  assert.throws(() => resolveTag({ json: () => ({ object: { type: 'tag', sha: tagSha } }) }, 'v0.1.1'), /chain/);
  let count = 0;
  assert.throws(() => resolveTag({ json: () => ({ object: { type: 'tag', sha: (++count).toString(16).padStart(40, '0') } }) }, 'v0.1.1'), /chain/);
  assert.equal(count, 17);
  const text = '{"test":true}', asset = { id: 1, name: 'storage-android-release.json', size: Buffer.byteLength(text), state: 'uploaded', digest: `sha256:${hash(text)}` };
  const client = githubClient('owner/repo', { run: () => ({ status: 0, stdout: text }) });
  assert.deepEqual(client.manifest(asset), { test: true });
  assert.throws(() => client.manifest({ ...asset, digest: `sha256:${'0'.repeat(64)}` }), /digest/);
  assert.throws(() => client.manifest({ ...asset, size: 65537 }), /large/);
});

test('separate platform publication sources preserve unchanged Android identity while rebuilding Windows', async t => {
  const { deriveSource, createManifest, select } = await import(moduleUrl);
  const root = fixture(t); commit(root); const windowsSource = deriveSource(root);
  const windowsDirectory = payloadDirectory(t, root, 'windows'); const windows = createManifest({ root, platform: 'windows', directory: windowsDirectory });
  commit(root, 'apps/android/new'); const androidSource = deriveSource(root);
  const androidDirectory = payloadDirectory(t, root, 'android', androidSource.version); const android = createManifest({ root, platform: 'android', directory: androidDirectory });
  commit(root, 'apps/windows/new'); commit(root, 'docs/only.md');
  const api = {
    list(path) {
      if (path === 'releases') return [{ id: 1, tag_name: windowsSource.tag, target_commitish: windowsSource.sha, draft: false, prerelease: false }, { id: 2, tag_name: androidSource.tag, target_commitish: androidSource.sha, draft: false, prerelease: false }];
      return path.includes('/1/') ? manifestAssets(windows) : manifestAssets(android);
    },
    manifest(asset) { return asset.name.includes('android') ? android : windows; },
    json(path) { return { object: { type: 'commit', sha: path.endsWith(androidSource.tag) ? androidSource.sha : windowsSource.sha } }; },
  };
  const result = select({ root, repository: 'owner/repo', api });
  assert.equal(result.android, false); assert.equal(result.windows, true);
  assert.equal(result.reasons.android.baseline, androidSource.sha); assert.equal(result.reasons.windows.baseline, windowsSource.sha);
});

test('manifest schema rejects traversal, control characters, case collisions, duplicates and stale versions', async t => {
  const { deriveSource, createManifest, validateManifest } = await import(moduleUrl);
  const root = fixture(t); commit(root); const source = deriveSource(root);
  const directory = payloadDirectory(t, root, 'android'), manifest = createManifest({ root, platform: 'android', directory });
  for (const name of ['../escape.apk', '/absolute.apk', 'bad\nname.apk', 'Storage-Browser-0.1.1.apk', 'storage-browser-0.1.1.apk']) {
    const invalid = structuredClone(manifest); invalid.files[0].name = name;
    assert.throws(() => validateManifest(invalid, 'android', source), /inventory/);
  }
  for (const mutation of [{ version: '0.1.0' }, { signing: 'debug' }, { schemaVersion: 2 }, { count: 0 }]) assert.throws(() => validateManifest({ ...manifest, ...mutation }, 'android', source), /source|schema|version/);
  const malformed = structuredClone(manifest); malformed.files[0].sha256 = 'A'.repeat(64);
  assert.throws(() => validateManifest(malformed, 'android', source), /metadata/);
});

test('real CLI derives committed fixture identity and version ceiling refuses wrapped native versions', async t => {
  const { deriveSource } = await import(moduleUrl); const root = fixture(t); commit(root);
  mkdirSync(join(root, 'scripts')); writeFileSync(join(root, 'scripts/release.mjs'), readFileSync(moduleUrl));
  git(root, 'add', '.'); git(root, 'commit', '-m', 'CLI fixture');
  const child = spawnSync(process.execPath, [join(root, 'scripts/release.mjs'), 'version'], { encoding: 'utf8' });
  assert.equal(child.status, 0, child.stderr); assert.equal(JSON.parse(child.stdout).count, 2); assert.equal(JSON.parse(child.stdout).sha, git(root, 'rev-parse', 'HEAD'));
  for (const count of ['0', '65535', '9007199254740993']) {
    const run = (cmd, args, opts) => args.includes('--count') ? { status: 0, stdout: count } : spawnSync(cmd, args, opts);
    assert.throws(() => deriveSource(root, { run }), /1\.\.65534/);
  }
});

test('older publisher exits without creating a release when a newer platform publication already includes it', async t => {
  const { deriveSource, createManifest, publish } = await import(moduleUrl);
  const root = fixture(t); const old = commit(root), androidDirectory = payloadDirectory(t, root, 'android');
  createManifest({ root, platform: 'android', directory: androidDirectory });
  commit(root, 'apps/android/new'); const newer = deriveSource(root), newerDirectory = payloadDirectory(t, root, 'android', newer.version);
  const manifest = createManifest({ root, platform: 'android', directory: newerDirectory }), fake = fakeGitHub(newer, { existing: true });
  for (const name of [...manifest.files.map(f => f.name), 'storage-android-release.json']) fake.seed(name, readFileSync(join(newerDirectory, name)));
  git(root, 'checkout', '--detach', old);
  const result = publish({ root, repository: 'owner/repo', androidDirectory, run: fake.run });
  assert.deepEqual(result.published, []); assert.deepEqual(result.skipped, ['android']); assert.equal(fake.writes.length, 0);
});

test('version uses full committed Git count and refuses unborn/shallow history', async t => {
  const { deriveSource, main } = await import(moduleUrl);
  const root = fixture(t);
  assert.throws(() => deriveSource(root), /committed|HEAD|unborn/);
  commit(root); const sha = commit(root, 'docs/readme.md');
  assert.deepEqual(deriveSource(root), { count: 2, version: '0.1.2', tag: 'v0.1.2', sha });
  const output = join(scratch, `storage-output-${process.pid}`); t.after(() => rmSync(output, { force: true }));
  main(['version', '--github-output', output], { root, output: () => {} });
  assert.match(readFileSync(output, 'utf8'), /version=0.1.2\ntag=v0.1.2\nsha=/);
  const shallow = join(root, 'shallow');
  const r = spawnSync('git', ['clone', '--depth=1', `file://${root}`, shallow], { encoding: 'utf8' }); assert.equal(r.status, 0, r.stderr);
  assert.throws(() => deriveSource(shallow), /[Ff]ull|shallow/);
});
