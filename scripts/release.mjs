#!/usr/bin/env node
// Immutable platform publications; only publish writes to GitHub.
import { spawnSync } from 'node:child_process';
import { appendFileSync, readFileSync, writeFileSync, readdirSync, lstatSync, openSync, readSync, closeSync, mkdtempSync, mkdirSync, copyFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SHA = /^[a-f0-9]{40}$/;
function invoke(run, command, args, extra = {}) {
  const result = run(command, args, { encoding: 'utf8', shell: false, timeout: 120_000, maxBuffer: 4 * 1024 * 1024, ...extra });
  if (result.error || result.status !== 0) throw new Error(`${command} failed: ${result.error?.message || result.stderr || result.status}`);
  return result.stdout;
}
export function deriveSource(root = ROOT, { run = spawnSync } = {}) {
  const git = (...args) => invoke(run, 'git', ['-C', resolve(root), ...args]).trim();
  if (git('rev-parse', '--is-shallow-repository') !== 'false') throw new Error('Full committed history required; shallow checkout refused.');
  let sha;
  try { sha = git('rev-parse', '--verify', 'HEAD^{commit}'); }
  catch (error) { throw new Error(`Committed HEAD required (unborn repository): ${error.message}`); }
  const count = Number(git('rev-list', '--count', sha));
  if (!SHA.test(sha) || !Number.isSafeInteger(count) || count < 1 || count > 65534) throw new Error('Committed source count must be 1..65534 and SHA must be lowercase SHA-1.');
  return { count, version: `0.1.${count}`, tag: `v0.1.${count}`, sha };
}
const PLATFORMS = ['android', 'windows'];
const HASH = /^[a-f0-9]{64}$/;
const MAX_FILE = 4 * 1024 ** 3;
const MANIFEST_LIMIT = 65536;
function platformNames(platform, source) {
  if (!PLATFORMS.includes(platform)) throw new Error('Invalid platform.');
  const payloads = platform === 'android'
    ? [`Storage-Service-${source.version}.apk`, `Storage-Browser-${source.version}.apk`]
    : ['StorageWindows-win-x64-Setup.exe', 'StorageWindows-win-x64-Setup.exe.sha256', `StorageWindows-${source.version}-win-x64-full.nupkg`, 'releases.win-x64.json', 'assets.win-x64.json', 'RELEASES-win-x64'];
  return { payloads, sums: `Storage-${platform}-SHA256SUMS`, manifestName: `storage-${platform}-release.json` };
}
function safeName(name) {
  return typeof name === 'string' && name.length <= 200 && /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(name) && name !== '.' && name !== '..';
}
function exactNames(actual, expected) {
  if (!Array.isArray(actual) || actual.length !== expected.length || actual.some(n => !safeName(n)) || new Set(actual.map(n => n.toLowerCase())).size !== actual.length || [...actual].sort().join('\0') !== [...expected].sort().join('\0')) throw new Error('Unexpected or incomplete platform inventory.');
}
function fileRecord(directory, name) {
  if (!safeName(name)) throw new Error('Unsafe asset name.');
  const path = resolve(directory, name), before = lstatSync(path);
  if (!before.isFile() || before.isSymbolicLink() || before.nlink !== 1 || before.size < 1 || before.size > MAX_FILE) throw new Error(`Expected nonempty bounded regular file without links: ${name}`);
  const fd = openSync(path, 'r'), hash = createHash('sha256'), buffer = Buffer.alloc(1024 * 1024);
  let bytes = 0;
  try { let n; while ((n = readSync(fd, buffer, 0, buffer.length, null)) !== 0) { bytes += n; if (bytes > MAX_FILE) throw new Error('File too large.'); hash.update(buffer.subarray(0, n)); } }
  finally { closeSync(fd); }
  const after = lstatSync(path);
  if (bytes !== before.size || before.ino !== after.ino || before.size !== after.size || before.mtimeMs !== after.mtimeMs || !after.isFile() || after.isSymbolicLink()) throw new Error(`Local file changed during hash: ${name}`);
  return { name, bytes, sha256: hash.digest('hex') };
}
function smallText(directory, name) {
  if (fileRecord(directory, name).bytes > MANIFEST_LIMIT) throw new Error(`Metadata too large: ${name}`);
  return readFileSync(resolve(directory, name), 'utf8');
}
function sumText(records) { return records.map(f => `${f.sha256}  ${f.name}\n`).join(''); }
function checkSidecar(directory, platform, records) {
  if (platform === 'windows' && smallText(directory, 'StorageWindows-win-x64-Setup.exe.sha256') !== sumText([records.find(f => f.name === 'StorageWindows-win-x64-Setup.exe')])) throw new Error('Windows installer sidecar hash mismatch.');
}
export function validateManifest(manifest, platform, source) {
  const { payloads, sums } = platformNames(platform, source);
  if (!manifest || Object.keys(manifest).sort().join(',') !== ['schemaVersion', 'platform', 'version', 'count', 'tag', 'sourceCommit', 'signing', 'files'].sort().join(',') || manifest.schemaVersion !== 1 || manifest.platform !== platform || manifest.version !== source.version || manifest.count !== source.count || manifest.tag !== source.tag || manifest.sourceCommit !== source.sha || !SHA.test(manifest.sourceCommit) || manifest.signing !== (platform === 'android' ? 'release-key' : 'unsigned')) throw new Error('Manifest source/version/schema mismatch.');
  exactNames(manifest.files?.map(f => f?.name), [...payloads, sums]);
  for (const file of manifest.files) {
    if (!file || Object.keys(file).sort().join(',') !== 'bytes,name,sha256' || !Number.isSafeInteger(file.bytes) || file.bytes < 1 || file.bytes > MAX_FILE || !HASH.test(file.sha256)) throw new Error('Invalid manifest file metadata.');
  }
  return manifest;
}
function directoryNames(directory) {
  if (!lstatSync(directory).isDirectory() || lstatSync(directory).isSymbolicLink()) throw new Error('Expected real directory.');
  const names = readdirSync(directory);
  for (const name of names) fileRecord(directory, name);
  return names;
}
export function createManifest({ root = ROOT, platform, directory, run = spawnSync }) {
  const source = deriveSource(root, { run }), { payloads, sums, manifestName } = platformNames(platform, source);
  const names = directoryNames(directory);
  exactNames(names.filter(n => n !== sums && n !== manifestName), payloads);
  if (names.includes(manifestName)) return validateDirectory({ root, platform, directory, run }).manifest;
  const records = payloads.map(name => fileRecord(directory, name)); checkSidecar(directory, platform, records);
  const checksum = sumText(records);
  if (names.includes(sums) && smallText(directory, sums) !== checksum) throw new Error('Existing checksum differs; refuse overwrite.');
  if (!names.includes(sums)) writeFileSync(resolve(directory, sums), checksum, { flag: 'wx' });
  const manifest = { schemaVersion: 1, platform, version: source.version, count: source.count, tag: source.tag, sourceCommit: source.sha, signing: platform === 'android' ? 'release-key' : 'unsigned', files: [...records, fileRecord(directory, sums)] };
  writeFileSync(resolve(directory, manifestName), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
  return validateDirectory({ root, platform, directory, run }).manifest;
}
export function validateDirectory({ root = ROOT, platform, directory, run = spawnSync }) {
  const source = deriveSource(root, { run }), { payloads, sums, manifestName } = platformNames(platform, source);
  exactNames(directoryNames(directory), [...payloads, sums, manifestName]);
  const manifest = validateManifest(JSON.parse(smallText(directory, manifestName)), platform, source);
  const files = manifest.files.map(f => {
    const record = fileRecord(directory, f.name);
    if (record.bytes !== f.bytes || record.sha256 !== f.sha256) throw new Error(`Local file hash/metadata mismatch: ${f.name}`);
    return record;
  });
  checkSidecar(directory, platform, files);
  if (smallText(directory, sums) !== sumText(payloads.map(name => files.find(f => f.name === name)))) throw new Error('Platform checksum content mismatch.');
  return { source, manifest, files: [...files, fileRecord(directory, manifestName)] };
}

function repositoryName(repository) {
  if (typeof repository !== 'string' || !/^[A-Za-z0-9_][A-Za-z0-9_.-]*\/[A-Za-z0-9_][A-Za-z0-9_.-]*$/.test(repository) || repository.split('/').some(p => p === '.' || p === '..')) throw new Error('Invalid GitHub repository.');
  return repository;
}
export function githubClient(repository, { run = spawnSync } = {}) {
  repositoryName(repository);
  const request = (path, { optional = false, binary = false } = {}) => {
    const args = ['api', `repos/${repository}/${path}`];
    if (binary) args.push('-H', 'Accept: application/octet-stream');
    const result = run('gh', args, { encoding: 'utf8', shell: false, timeout: 120_000, maxBuffer: binary ? MANIFEST_LIMIT : 4 * 1024 * 1024 });
    if (result.error || result.status !== 0) {
      if (optional && !result.error && /\(HTTP 404\)/.test(result.stderr || '')) return null;
      throw new Error(`GitHub read failed (${path}): ${result.error?.message || result.stderr || result.status}`);
    }
    if (Buffer.byteLength(result.stdout) > (binary ? MANIFEST_LIMIT : 4 * 1024 * 1024)) throw new Error('GitHub response exceeds byte limit.');
    return result.stdout;
  };
  return {
    json(path, options) { const text = request(path, options); return text === null ? null : JSON.parse(text); },
    list(path) {
      const all = [];
      for (let page = 1; page <= 20; page++) {
        const values = JSON.parse(request(`${path}?per_page=100&page=${page}`));
        if (!Array.isArray(values) || values.length > 100) throw new Error('Invalid GitHub inventory page.');
        all.push(...values);
        if (values.length < 100) return all;
      }
      throw new Error('GitHub pagination limit reached (20 full pages); refusing truncated inventory.');
    },
    manifest(asset) {
      checkAsset(asset);
      if (asset.size > MANIFEST_LIMIT) throw new Error('Manifest asset too large.');
      const text = request(`releases/assets/${asset.id}`, { binary: true });
      if (Buffer.byteLength(text) !== asset.size || (asset.digest && asset.digest !== `sha256:${createHash('sha256').update(text).digest('hex')}`)) throw new Error('Manifest asset digest/size mismatch.');
      return JSON.parse(text);
    },
  };
}
function checkAsset(asset, record) {
  if (!asset || !Number.isSafeInteger(asset.id) || asset.id < 1 || !safeName(asset.name) || asset.state !== 'uploaded' || !Number.isSafeInteger(asset.size) || asset.size < 1 || asset.size > MAX_FILE || (asset.digest != null && !/^sha256:[a-f0-9]{64}$/.test(asset.digest))) throw new Error('Invalid uploaded asset metadata.');
  if (record && (asset.size !== record.bytes || (asset.digest != null && asset.digest !== `sha256:${record.sha256}`))) throw new Error(`Published asset digest/size mismatch: ${asset.name}`);
}
function uniqueAsset(assets, name, { optional = false } = {}) {
  const found = assets.filter(a => typeof a.name === 'string' && a.name.toLowerCase() === name.toLowerCase());
  if (!found.length && optional) return null;
  if (found.length !== 1 || found[0].name !== name) throw new Error(`Missing or duplicate published asset: ${name}`);
  checkAsset(found[0]); return found[0];
}
export function resolveTag(api, tag, { optional = false } = {}) {
  const ref = api.json(`git/ref/tags/${tag}`, { optional });
  if (ref === null && optional) return null;
  let object = ref?.object; const seen = new Set();
  while (object?.type === 'tag') {
    if (!SHA.test(object.sha) || seen.has(object.sha) || seen.size >= 16) throw new Error('Invalid or excessive annotated tag chain.');
    seen.add(object.sha); object = api.json(`git/tags/${object.sha}`)?.object;
  }
  if (object?.type !== 'commit' || !SHA.test(object.sha)) throw new Error('Publication tag must resolve to commit.');
  return object.sha;
}
function stableReleases(api) {
  const releases = api.list('releases').filter(r => r.draft === false && r.prerelease === false && /^v0\.1\.[1-9][0-9]*$/.test(r.tag_name));
  const seen = new Set();
  for (const r of releases) {
    const count = Number(r.tag_name.slice(5));
    if (!Number.isSafeInteger(count) || count > 65534 || !Number.isSafeInteger(r.id) || r.id < 1 || seen.has(r.tag_name)) throw new Error('Invalid or duplicate stable release inventory.');
    seen.add(r.tag_name);
  }
  return releases.sort((a, b) => Number(b.tag_name.slice(5)) - Number(a.tag_name.slice(5)));
}
export function publishedBaseline(platform, { api }) {
  if (!PLATFORMS.includes(platform)) throw new Error('Invalid platform.');
  for (const release of stableReleases(api)) {
    const count = Number(release.tag_name.slice(5));
    const source = { count, version: `0.1.${count}`, tag: release.tag_name, sha: release.target_commitish };
    const assets = api.list(`releases/${release.id}/assets`);
    const marker = uniqueAsset(assets, `storage-${platform}-release.json`, { optional: true });
    if (!marker) continue; // A platform is not published until its manifest is present.
    if (!SHA.test(source.sha)) throw new Error('Published source must be an exact commit SHA.');
    const manifest = validateManifest(api.manifest(marker), platform, source);
    for (const record of manifest.files) checkAsset(uniqueAsset(assets, record.name), record);
    if (resolveTag(api, source.tag) !== source.sha) throw new Error('Published tag/source mismatch.');
    return source.sha;
  }
  return null;
}
export function affectedPlatforms(paths) {
  const affected = new Set();
  for (const path of paths) {
    if (path.startsWith('docs/') || /(?:^|\/)(?:README[^/]*|AGENTS(?:\.[^/]*)?|VALIDATION(?:\.[^/]*)?|RELEASING(?:\.[^/]*)?)$/.test(path)) continue;
    if (path.startsWith('apps/android/')) { affected.add('android'); continue; }
    if (path.startsWith('apps/windows/')) { affected.add('windows'); continue; }
    if (/^(?:scripts|tests)\//.test(path)) {
      const android = /android/i.test(path), windows = /windows/i.test(path);
      if (android !== windows) { affected.add(android ? 'android' : 'windows'); continue; }
    }
    for (const platform of PLATFORMS) affected.add(platform); // Unknown inputs and whole CI workflow are shared.
  }
  return PLATFORMS.filter(p => affected.has(p));
}
export function decideFromBaseline({ root = ROOT, platform, baseline, run = spawnSync }) {
  if (!PLATFORMS.includes(platform) || (baseline !== null && !SHA.test(baseline))) throw new Error('Invalid platform/baseline.');
  const source = deriveSource(root, { run }), head = source.sha;
  const result = (build, reason, paths = []) => ({ build, baseline, head, reason, paths });
  if (baseline === null) return result(true, 'no verified platform publication');
  const ancestor = (a, b) => {
    const r = run('git', ['-C', resolve(root), 'merge-base', '--is-ancestor', a, b], { encoding: 'utf8', shell: false, timeout: 120_000, maxBuffer: 1024 * 1024 });
    if (r.error || ![0, 1].includes(r.status)) throw new Error('Cannot establish published source ancestry; fetch full source history.');
    return r.status === 0;
  };
  if (!ancestor(baseline, head)) {
    if (ancestor(head, baseline)) return result(false, 'newer publication already includes this source');
    throw new Error('Published source diverges from HEAD; inspect source history.');
  }
  const paths = invoke(run, 'git', ['-C', resolve(root), 'diff', '--name-only', '-z', '--no-renames', baseline, head, '--']).split('\0').filter(Boolean);
  const build = affectedPlatforms(paths).includes(platform);
  return result(build, build ? 'inputs changed since verified publication' : 'platform inputs unchanged', paths);
}
export function select({ root = ROOT, repository, target = 'auto', run = spawnSync, api = githubClient(repository, { run }) }) {
  repositoryName(repository);
  if (!['auto', 'all', ...PLATFORMS].includes(target)) throw new Error('Invalid target.');
  const reasons = Object.fromEntries(PLATFORMS.map(platform => [platform, decideFromBaseline({ root, platform, baseline: publishedBaseline(platform, { api }), run })]));
  return { android: target === 'auto' ? reasons.android.build : ['all', 'android'].includes(target), windows: target === 'auto' ? reasons.windows.build : ['all', 'windows'].includes(target), android_changed: reasons.android.build, windows_changed: reasons.windows.build, reasons };
}

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]));
  return value;
}
function assetSnapshot(asset) { const { download_count, ...rest } = asset; return JSON.stringify(canonical(rest)); }
function releaseSnapshot(release) {
  // Asset additions and automatic timestamps are not edits to release policy/notes.
  const { assets, updated_at, ...rest } = release;
  return JSON.stringify(canonical(rest));
}
function assertRelease(release, source, api) {
  if (!release || !Number.isSafeInteger(release.id) || release.id < 1 || release.tag_name !== source.tag || release.target_commitish !== source.sha || release.draft !== false || release.prerelease !== false) throw new Error('Destination must be a stable, non-draft release for the exact source/tag.');
  if (resolveTag(api, source.tag) !== source.sha) throw new Error('Destination tag/source mismatch.');
}
export function publish({ root = ROOT, repository, androidDirectory, windowsDirectory, run = spawnSync }) {
  repositoryName(repository);
  const directories = { android: androidDirectory, windows: windowsDirectory };
  const selected = PLATFORMS.filter(p => directories[p] !== undefined);
  if (!selected.length) return { published: [], skipped: [], reason: 'no successful platform artifacts; no release created' };
  const source = deriveSource(root, { run }), api = githubClient(repository, { run });
  const clean = () => {
    const status = invoke(run, 'git', ['-C', resolve(root), 'status', '--porcelain', '--untracked-files=all']);
    if (status.trim()) throw new Error('Publication requires a clean committed Git checkout (ignored dist outputs are allowed).');
    if (JSON.stringify(deriveSource(root, { run })) !== JSON.stringify(source)) throw new Error('Source identity changed during publication.');
  };
  clean();
  const workspace = mkdtempSync(resolve(process.env.TMPDIR || tmpdir(), 'storage-publish-'));
  const published = [], skipped = [], inventories = {}, errors = [];
  try {
    // Validate and snapshot every local input before any external writes.
    for (const platform of selected) {
      const inventory = validateDirectory({ root, platform, directory: directories[platform], run });
      const snapshot = resolve(workspace, platform); mkdirSync(snapshot);
      for (const file of inventory.files) copyFileSync(resolve(directories[platform], file.name), resolve(snapshot, file.name));
      inventories[platform] = { ...validateDirectory({ root, platform, directory: snapshot, run }), directory: snapshot };
      const baseline = publishedBaseline(platform, { api });
      const decision = decideFromBaseline({ root, platform, baseline, run });
      if (decision.reason === 'newer publication already includes this source') skipped.push(platform);
    }
    const active = selected.filter(p => !skipped.includes(p));
    if (!active.length) return { ...source, published, skipped };
    let release = api.json(`releases/tags/${source.tag}`, { optional: true });
    const existingTag = resolveTag(api, source.tag, { optional: true });
    if (existingTag !== null && existingTag !== source.sha) throw new Error('Existing destination tag points to a different source.');
    const created = release === null;
    if (release) assertRelease(release, source, api);
    else {
      clean();
      const body = `Storage ${source.version}\n\nEarly local-storage release. Device pairing, replication and full distributed feature acceptance are not claimed. In-app automatic updating is not claimed.\n\nWindows installers are unsigned; Windows may show an unknown-publisher/SmartScreen warning. Verify the published SHA-256 checksums before installation. Android APKs use the configured Storage release key.\n\nOnly successful selected platform outputs are attached. Unchanged platforms retain their previous release, version and source identity; absence here does not imply a new platform binary.\n\nSource: ${source.sha}`;
      invoke(run, 'gh', ['api', `repos/${repository}/releases`, '--method', 'POST', '--input', '-'], { input: JSON.stringify({ tag_name: source.tag, target_commitish: source.sha, name: `Storage ${source.version}`, body, draft: false, prerelease: false, generate_release_notes: false, make_latest: 'false' }) });
      release = api.json(`releases/tags/${source.tag}`); assertRelease(release, source, api);
    }
    const originalRelease = releaseSnapshot(release);
    const originalAssets = api.list(`releases/${release.id}/assets`);
    const preserve = () => {
      const current = api.json(`releases/tags/${source.tag}`); assertRelease(current, source, api);
      if (releaseSnapshot(current) !== originalRelease) throw new Error('Release metadata changed; refusing to continue.');
      const assets = api.list(`releases/${release.id}/assets`);
      for (const before of originalAssets) {
        const matches = assets.filter(a => a.id === before.id);
        if (matches.length !== 1 || assetSnapshot(matches[0]) !== assetSnapshot(before)) throw new Error(`Preexisting asset metadata changed: ${before.name}`);
      }
      return assets;
    };
    const readback = (file, assets) => {
      const asset = uniqueAsset(assets, file.name); checkAsset(asset, file);
      const download = mkdtempSync(resolve(workspace, 'readback-'));
      try {
        invoke(run, 'gh', ['release', 'download', source.tag, '--repo', repository, '--pattern', file.name, '--dir', download]);
        exactNames(readdirSync(download), [file.name]);
        const actual = fileRecord(download, file.name);
        if (actual.bytes !== file.bytes || actual.sha256 !== file.sha256) throw new Error(`Remote downloaded hash/size mismatch: ${file.name}`);
      } finally { rmSync(download, { recursive: true, force: true }); }
      const current = uniqueAsset(preserve(), file.name);
      if (current.id !== asset.id || assetSnapshot(current) !== assetSnapshot(asset)) throw new Error(`Remote asset changed during readback: ${file.name}`);
    };
    // Each platform's last file is its verified-baseline marker. Never upload it
    // until all payloads/checksums have passed remote readback and preservation.
    for (const platform of active) {
      try {
        const inventory = inventories[platform];
        for (const file of inventory.files) {
          const assets = preserve();
          if (uniqueAsset(assets, file.name, { optional: true })) readback(file, assets);
        }
        for (const file of inventory.files) {
          let assets = preserve(), asset = uniqueAsset(assets, file.name, { optional: true });
          if (!asset) {
            clean();
            const local = fileRecord(inventory.directory, file.name);
            if (local.bytes !== file.bytes || local.sha256 !== file.sha256) throw new Error(`Snapshot file changed: ${file.name}`);
            invoke(run, 'gh', ['release', 'upload', source.tag, resolve(inventory.directory, file.name), '--repo', repository]);
            assets = preserve();
          }
          readback(file, assets);
        }
        for (const file of inventory.files) readback(file, preserve());
        published.push(platform);
      } catch (error) { errors.push(`${platform}: ${error.message}`); }
    }
    preserve(); clean();
    // New release only: never edit existing Latest policy or let an older run
    // displace a newer numeric stable release. Parent serializes this whole job.
    if (created && published.length && stableReleases(api)[0]?.tag_name === source.tag) {
      invoke(run, 'gh', ['api', `repos/${repository}/releases/${release.id}`, '--method', 'PATCH', '--input', '-'], { input: JSON.stringify({ make_latest: 'true' }) });
      if (api.json('releases/latest').id !== release.id) throw new Error('Latest release promotion did not read back.');
      preserve();
    }
    if (errors.length) throw new Error(`Publication incomplete (verified platforms: ${published.join(', ') || 'none'}): ${errors.join('; ')}`);
    return { ...source, published, skipped };
  } finally { rmSync(workspace, { recursive: true, force: true }); }
}

export function main(argv = process.argv.slice(2), { root = ROOT, run = spawnSync, output = text => console.log(text) } = {}) {
  const [command, ...rest] = argv, options = {};
  const allowed = { version: ['--github-output'], manifest: ['--platform', '--directory'], select: ['--repository', '--target', '--github-output'], publish: ['--repository', '--android-directory', '--windows-directory'] };
  if (!Object.hasOwn(allowed, command)) throw new Error('Usage: release.mjs version|select|manifest|publish [options].');
  for (let i = 0; i < rest.length; i += 2) {
    const key = rest[i], value = rest[i + 1];
    if (!allowed[command].includes(key) || Object.hasOwn(options, key) || typeof value !== 'string' || !value || value.startsWith('-') || /[\r\n\0]/.test(value)) throw new Error('Invalid or duplicate release option.');
    options[key] = value;
  }
  let result;
  if (command === 'version') result = deriveSource(root, { run });
  if (command === 'manifest') {
    if (!options['--directory']) throw new Error('--directory is required.');
    result = createManifest({ root, platform: options['--platform'], directory: resolve(options['--directory']), run });
  }
  if (command === 'select') result = select({ root, repository: options['--repository'], target: options['--target'] ?? 'auto', run });
  if (command === 'publish') result = publish({ root, repository: options['--repository'], androidDirectory: options['--android-directory'], windowsDirectory: options['--windows-directory'], run });
  if (options['--github-output']) appendFileSync(options['--github-output'], Object.entries(result).map(([k, v]) => `${k}=${typeof v === 'object' ? JSON.stringify(v) : v}\n`).join(''));
  output(JSON.stringify(result, null, 2)); return result;
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
