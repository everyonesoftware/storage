import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const moduleUrl = new URL('../scripts/current-downloads.mjs', import.meta.url);
const repository = 'owner/repo';
function fixture() {
  const releases = [], assets = new Map(), manifests = new Map();
  const state = { releases, assets, manifests };
  state.add = (count, platforms, { legacyInstaller = false } = {}) => {
    const version = `0.1.${count}`, tag = `v${version}`, sha = String(count).padStart(40, 'a');
    const release = { id: count, tag_name: tag, target_commitish: sha, draft: false, prerelease: false, body: `Notes ${count}\n` };
    releases.push(release); const files = []; assets.set(count, files);
    for (const platform of platforms) {
      const installer = legacyInstaller ? 'StorageWindows-win-x64-Setup.exe' : `StorageWindows-${version}-win-x64-Setup.exe`;
      const names = platform === 'android' ? [`Storage-Service-${version}.apk`, `Storage-Browser-${version}.apk`, 'Storage-android-SHA256SUMS'] : [installer, `${installer}.sha256`, `StorageWindows-${version}-win-x64-full.nupkg`, 'releases.win-x64.json', 'assets.win-x64.json', 'RELEASES-win-x64', 'Storage-windows-SHA256SUMS'];
      const manifest = { schemaVersion: 1, platform, version, count, tag, sourceCommit: sha, signing: platform === 'android' ? 'release-key' : 'unsigned', files: names.map(name => ({ name, bytes: 10, sha256: 'b'.repeat(64) })) };
      const records = [...manifest.files, { name: `storage-${platform}-release.json`, bytes: 10, sha256: 'c'.repeat(64) }];
      for (const record of records) {
        const asset = { id: count * 100 + files.length + 1, name: record.name, state: 'uploaded', size: record.bytes, digest: `sha256:${record.sha256}`, browser_download_url: `https://github.com/${repository}/releases/download/${tag}/${record.name}` };
        files.push(asset); if (record.name.endsWith('-release.json')) manifests.set(asset.id, manifest);
      }
    }
    return release;
  };
  state.api = {
    list(path) { if (path === 'releases') return structuredClone(releases); const match = /^releases\/(\d+)\/assets$/.exec(path); if (match) return structuredClone(assets.get(Number(match[1]))); throw Error(path); },
    manifest(asset) { return structuredClone(manifests.get(asset.id)); },
    json(path) { const tag = path.replace('git/ref/tags/', ''); const release = releases.find(r => r.tag_name === tag); if (release) return { object: { type: 'commit', sha: release.target_commitish } }; throw Error(path); },
  };
  return state;
}

test('selects verified platforms independently in numeric stable order and retains original URLs', async () => {
  const { collectDownloads, renderDownloads } = await import(moduleUrl);
  const f = fixture(); f.add(9, ['android']); f.add(12, ['windows']); f.add(20, []); f.add(30, ['android']).prerelease = true;
  const inventory = collectDownloads({ repository, api: f.api });
  assert.equal(inventory.android.version, '0.1.9'); assert.equal(inventory.windows.version, '0.1.12');
  assert.equal(inventory.android.service, 'https://github.com/owner/repo/releases/download/v0.1.9/Storage-Service-0.1.9.apk');
  const table = renderDownloads(inventory);
  assert.match(table, /Storage-Browser-0\.1\.9\.apk/); assert.match(table, /Storage-android-SHA256SUMS/);
  assert.match(table, /StorageWindows-0\.1\.12-win-x64-Setup\.exe\.sha256/);  assert.match(table, /service first/i); assert.match(table, /unsigned/i);
});

test('preserves original legacy installer links and rejects mixed or wrong-version inventories', async () => {
  const { collectDownloads } = await import(moduleUrl);
  const legacy = fixture(); legacy.add(12, ['windows'], { legacyInstaller: true });
  assert.equal(collectDownloads({ repository, api: legacy.api }).windows.url, 'https://github.com/owner/repo/releases/download/v0.1.12/StorageWindows-win-x64-Setup.exe');
  for (const replacement of ['StorageWindows-win-x64-Setup.exe.sha256', 'StorageWindows-0.1.11-win-x64-Setup.exe.sha256']) {
    const f = fixture(); f.add(12, ['windows']);
    f.manifests.values().next().value.files[1].name = replacement;
    assert.throws(() => collectDownloads({ repository, api: f.api }), /inventory/);
  }
});

test('fails closed on malformed stable inventory instead of silently selecting older publications', async () => {
  const { collectDownloads } = await import(moduleUrl);
  for (const mutate of [
    f => { f.releases[0].draft = 'false'; },
    f => { f.releases.push({ ...f.releases[0], id: 2 }); },
    f => { f.releases[0].tag_name = 'v0.1.01'; },
    f => { f.assets.get(1)[0].digest = `sha256:${'0'.repeat(64)}`; },
    f => { f.assets.get(1).splice(1, 1); },
    f => { f.assets.get(1)[0].browser_download_url = 'https://evil.invalid/download'; },
    f => { f.manifests.values().next().value.sourceCommit = 'd'.repeat(40); },
  ]) {
    const f = fixture(); f.add(1, ['android']); mutate(f);
    assert.throws(() => collectDownloads({ repository, api: f.api }), /inventory|asset|digest|source|URL|metadata|version/i);
  }
});

test('managed block replacement preserves surrounding text, bootstraps and rejects malformed markers', async () => {
  const { replaceDownloads, renderDownloads } = await import(moduleUrl);
  const start = '<!-- current-downloads:start -->', end = '<!-- current-downloads:end -->';
  const input = `# Storage\n\nBefore\n${start}\nold\n${end}\nAfter\n`;
  assert.equal(replaceDownloads(input, 'new'), input.replace('\nold\n', '\nnew\n'));
  const created = replaceDownloads('# Storage\n\nKeep\n', 'new', { readme: true });
  assert.match(created, /^# Storage\n\n<!-- current-downloads:start -->/); assert.ok(created.endsWith('Keep\n'));
  assert.equal(replaceDownloads(created, 'new'), created);
  assert.ok(replaceDownloads('Keep notes', 'new').endsWith('Keep notes'));
  for (const malformed of [start, end, end + start, start + start + end]) assert.throws(() => replaceDownloads(malformed, 'new'), /markers/);
  assert.match(renderDownloads({}), /Not yet published/);
});

function refreshFixture() {
  const f = fixture(); f.add(9, ['android']); f.add(12, ['windows']);
  f.readme = '# Storage\n\nRemote branch prose\n'; f.sha = 'd'.repeat(40); f.writes = []; f.runs = [];
  f.reads = []; f.hook = () => {};
  const originalJson = f.api.json;
  f.api.json = path => {
    f.reads.push(path); f.hook(path);
    if (path.startsWith('actions/runs?')) return { workflow_runs: structuredClone(f.runs) };
    if (path === '') return { default_branch: 'main' };
    if (path === 'contents/README.md?ref=main') return { encoding: 'base64', sha: f.sha, content: Buffer.from(f.readme).toString('base64') };
    const match = /^releases\/(\d+)$/.exec(path);
    if (match) return structuredClone(f.releases.find(r => r.id === Number(match[1])));
    return originalJson(path);
  };
  f.api.write = (path, method, data) => {
    f.writes.push({ path, method, data });
    if (path === 'contents/README.md') {
      assert.equal(data.sha, f.sha); assert.equal(data.branch, 'main');
      f.readme = Buffer.from(data.content, 'base64').toString(); f.sha = 'e'.repeat(40);
    } else {
      assert.equal(method, 'PATCH'); assert.deepEqual(Object.keys(data), ['body']);
      f.releases.find(r => path === `releases/${r.id}`).body = data.body;
    }
  };
  return f;
}

test('preview is read-only and publication updates only newest numeric notes and exact remote branch README idempotently', async () => {
  const { refreshDownloads } = await import(moduleUrl);
  const f = refreshFixture();
  const preview = refreshDownloads({ repository, api: f.api, localReadme: '# Storage\n\nLocal prose\n' });
  assert.ok(preview.readme.includes('Local prose')); assert.equal(preview.tag, 'v0.1.12'); assert.equal(f.writes.length, 0);
  const result = refreshDownloads({ repository, api: f.api, publish: true, localReadme: '# Stale\n' });
  assert.ok(result.readme.includes('Remote branch prose')); assert.ok(!result.readme.includes('Stale'));
  assert.equal(f.releases[0].body, 'Notes 9\n'); assert.ok(f.releases[1].body.endsWith('Notes 12\n'));
  assert.deepEqual(f.writes.map(w => [w.path, w.method]), [['releases/12', 'PATCH'], ['contents/README.md', 'PUT']]);
  assert.ok(f.reads.filter(p => p === 'releases/12').length >= 3);
  assert.equal(f.reads.at(-1), 'contents/README.md?ref=main');
  refreshDownloads({ repository, api: f.api, publish: true }); assert.equal(f.writes.length, 2);
});

test('defers all other active main runs but excludes only exact owning run ID, with bounded pagination', async () => {
  const { refreshDownloads } = await import(moduleUrl);
  const f = refreshFixture(); f.runs = [{ id: 42, status: 'in_progress' }, { id: 43, status: 'queued' }];
  assert.deepEqual(refreshDownloads({ repository, api: f.api, publish: true, currentRunId: '42' }), { deferred: true });
  assert.equal(f.writes.length, 0);
  f.runs.pop(); refreshDownloads({ repository, api: f.api, publish: true, currentRunId: '42' }); assert.equal(f.writes.length, 2);
  const g = refreshFixture(); g.runs = [{ id: 42, status: 'in_progress' }];
  assert.deepEqual(refreshDownloads({ repository, api: g.api, publish: true, currentRunId: '042' }), { deferred: true });
  const json = g.api.json; let pages = 0;
  g.api.json = path => path.startsWith('actions/runs?') ? (pages++, { workflow_runs: Array.from({ length: 100 }, (_, i) => ({ id: i + 1, status: 'completed' })) }) : json(path);
  assert.throws(() => refreshDownloads({ repository, api: g.api, publish: true }), /pagination|limit/); assert.equal(pages, 20);
});

test('rejects changed release targets before writing and validates stable source again at readback', async () => {
  const { refreshDownloads } = await import(moduleUrl);
  for (const mutate of [r => { r.draft = true; }, r => { r.tag_name = 'v0.1.11'; }, r => { r.target_commitish = 'f'.repeat(40); }]) {
    const f = refreshFixture(); let reads = 0;
    f.hook = path => { if (path === 'releases/12' && ++reads === 1) mutate(f.releases[1]); };
    assert.throws(() => refreshDownloads({ repository, api: f.api, publish: true }), /target|source|changed|stable/i);
    assert.equal(f.writes.length, 0);
  }
  const f = refreshFixture(); let reads = 0;
  f.hook = path => { if (path === 'releases/12' && ++reads === 3) f.releases[1].prerelease = true; };
  assert.throws(() => refreshDownloads({ repository, api: f.api, publish: true }), /readback|stable|target/i);
  assert.equal(f.writes.length, 1);
});

test('CLI defaults to read-only preview and writes three preview artifacts with honest empty rows', async t => {
  const { main } = await import(moduleUrl);
  const directory = mkdtempSync(join(process.env.TMPDIR, 'storage-downloads-test-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const calls = [];
  const run = (cmd, args, options) => {
    assert.equal(cmd, 'gh'); assert.equal(options.shell, false); calls.push(args);
    const path = args[1];
    assert.notEqual(path, `repos/${repository}/`, 'repository metadata uses canonical root endpoint');
    const response = path.includes('/releases?') ? [] : path.endsWith('/contents/README.md?ref=main') ? { encoding: 'base64', sha: 'a'.repeat(40), content: Buffer.from('# Storage\n\nRemote\n').toString('base64') } : { default_branch: 'main' };
    return { status: 0, stdout: JSON.stringify(response) };
  };
  const result = main(['--repository', repository, '--output-directory', directory], { run, output: () => {} });
  assert.equal(result.published, false); assert.ok(calls.every(args => !args.includes('--method')));
  assert.match(readFileSync(join(directory, 'README.md'), 'utf8'), /Not yet published/);
  assert.equal(readFileSync(join(directory, 'release-notes.md'), 'utf8'), '');
  assert.equal(JSON.parse(readFileSync(join(directory, 'downloads.json'))).android, null);
  for (const argv of [[], ['--repository', repository], ['--repository', '../bad', '--publish'], ['--repository', repository, '--publish', '--publish']]) assert.throws(() => main(argv, { run, output: () => {} }));
});

test('publication refuses observable note edits, README conflicts, blocked branches and corrupt readbacks honestly', async () => {
  const { refreshDownloads } = await import(moduleUrl);
  const changed = refreshFixture(); let noteReads = 0;
  changed.hook = path => { if (path === 'releases/12' && ++noteReads === 2) changed.releases[1].body += 'Human edit'; };
  assert.throws(() => refreshDownloads({ repository, api: changed.api, publish: true }), /changed/i); assert.equal(changed.writes.length, 0);
  for (const message of ['409 blob SHA changed', '403 protected branch']) {
    const f = refreshFixture(), write = f.api.write;
    f.api.write = (path, method, data) => { if (path === 'contents/README.md') throw Error(message); write(path, method, data); };
    assert.throws(() => refreshDownloads({ repository, api: f.api, publish: true }), new RegExp(message));
    assert.equal(f.writes.length, 1, 'partial completion is not reported as success');
    f.api.write = write; refreshDownloads({ repository, api: f.api, publish: true }); assert.equal(f.writes.length, 2, 'rerun repairs only README');
  }
  for (const target of ['release', 'readme']) {
    const f = refreshFixture(), write = f.api.write;
    f.api.write = (path, method, data) => { write(path, method, data); if (target === 'release') f.releases[1].body = 'tampered'; else if (path === 'contents/README.md') f.readme = '# tampered\n'; };
    assert.throws(() => refreshDownloads({ repository, api: f.api, publish: true }), /readback/i);
  }
  const denied = refreshFixture(); denied.api.json = () => { throw Error('403 Actions denied'); };
  assert.throws(() => refreshDownloads({ repository, api: denied.api, publish: true }), /403/); assert.equal(denied.writes.length, 0);
});

test('asset download counters changing during note preflight do not block safe body-only writes', async () => {
  const { refreshDownloads } = await import(moduleUrl);
  const f = refreshFixture(); let reads = 0;
  f.releases[1].assets = [{ download_count: 1 }];
  f.hook = path => { if (path === 'releases/12' && ++reads === 2) f.releases[1].assets[0].download_count++; };
  refreshDownloads({ repository, api: f.api, publish: true });
  assert.equal(f.writes.length, 2); assert.equal(f.releases[1].assets[0].download_count, 2);
});

test('malformed remote README bytes fail closed before any publication', async () => {
  const { refreshDownloads } = await import(moduleUrl);
  for (const content of [Buffer.from('# Storage\n').toString('base64') + '@@@', Buffer.concat([Buffer.from('# Storage\n'), Buffer.from([0xff, 0xfe])]).toString('base64')]) {
    const f = refreshFixture(), json = f.api.json;
    f.api.json = path => path.startsWith('contents/') ? { encoding: 'base64', sha: 'a'.repeat(40), content } : json(path);
    assert.throws(() => refreshDownloads({ repository, api: f.api, publish: true }), /README|UTF|base64/i);
    assert.equal(f.writes.length, 0);
  }
});
