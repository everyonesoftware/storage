#!/usr/bin/env node
import { publishedBaseline, resolveTag, githubClient } from './release.mjs';
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';

function validateRepository(repository) {
  if (typeof repository !== 'string' || !/^[A-Za-z0-9_][A-Za-z0-9_.-]*\/[A-Za-z0-9_][A-Za-z0-9_.-]*$/.test(repository) || repository.split('/').some(p => p === '.' || p === '..')) throw Error('Invalid repository.');
}
export function collectDownloads({ repository, api }) {
  validateRepository(repository);
  const releases = stableInventory(api);
  const verifiedApi = { ...api, list: path => path === 'releases' ? releases : api.list(path) };
  const inventory = { repository, android: null, windows: null };
  for (const platform of ['android', 'windows']) {
    const publication = publishedBaseline(platform, { api: verifiedApi, details: true });
    if (!publication) continue;
    const { source, assets } = publication;
    const url = name => {
      const asset = assets.find(a => a.name === name);
      const expected = `https://github.com/${repository}/releases/download/${source.tag}/${name}`;
      if (asset?.browser_download_url !== expected) throw Error('Invalid published download URL.');
      return asset.browser_download_url;
    };
    inventory[platform] = { version: source.version, tag: source.tag, sourceCommit: source.sha,
      ...(platform === 'android' ? { service: url(`Storage-Service-${source.version}.apk`), browser: url(`Storage-Browser-${source.version}.apk`), checksum: url('Storage-android-SHA256SUMS') } : { url: url('StorageWindows-win-x64-Setup.exe'), checksum: url('StorageWindows-win-x64-Setup.exe.sha256') }) };
  }
  return inventory;
}
function stableInventory(api) {
  const releases = api.list('releases');
  if (!Array.isArray(releases)) throw Error('Invalid release inventory.');
  const ids = new Set(), tags = new Set();
  for (const r of releases) {
    if (!r || typeof r.draft !== 'boolean' || typeof r.prerelease !== 'boolean' || typeof r.tag_name !== 'string' || !Number.isSafeInteger(r.id) || r.id < 1 || ids.has(r.id) || tags.has(r.tag_name)) throw Error('Invalid or duplicate release inventory.');
    ids.add(r.id); tags.add(r.tag_name);
    if (!r.draft && !r.prerelease && /^v0\.1\./.test(r.tag_name) && (!/^v0\.1\.[1-9][0-9]*$/.test(r.tag_name) || Number(r.tag_name.slice(5)) > 65534)) throw Error('Invalid stable version inventory.');
  }
  return releases.filter(r => !r.draft && !r.prerelease && /^v0\.1\.[1-9][0-9]*$/.test(r.tag_name)).sort((a, b) => Number(b.tag_name.slice(5)) - Number(a.tag_name.slice(5)));
}
function otherPublishers(api, currentRunId) {
  for (let page = 1; page <= 20; page++) {
    const runs = api.json(`actions/runs?branch=main&per_page=100&page=${page}`)?.workflow_runs;
    if (!Array.isArray(runs) || runs.length > 100 || runs.some(r => !Number.isSafeInteger(r.id) || r.id < 1 || typeof r.status !== 'string')) throw Error('Invalid Actions run inventory.');
    if (runs.some(r => r.status !== 'completed' && !(currentRunId && String(r.id) === currentRunId))) return true;
    if (runs.length < 100) return false;
  }
  throw Error('Actions pagination limit reached; refusing truncated inventory.');
}
export function refreshDownloads({ repository, api, publish = false, localReadme, currentRunId = process.env.GITHUB_RUN_ID ?? '' }) {
  validateRepository(repository);
  if (publish && otherPublishers(api, currentRunId)) return { deferred: true };
  const releases = stableInventory(api);
  const inventory = collectDownloads({ repository, api: { ...api, list: path => path === 'releases' ? releases : api.list(path) } });
  const table = renderDownloads(inventory), newest = releases[0];
  const target = newest ? api.json(`releases/${newest.id}`) : null;
  const checkTarget = value => {
    if (!value || value.id !== newest.id || value.tag_name !== newest.tag_name || value.target_commitish !== newest.target_commitish || value.draft !== false || value.prerelease !== false || !/^[a-f0-9]{40}$/.test(value.target_commitish) || (value.body != null && typeof value.body !== 'string')) throw Error('Stable release target changed or invalid source.');
    if (resolveTag(api, value.tag_name) !== value.target_commitish) throw Error('Release target tag/source mismatch.');
  };
  if (newest) checkTarget(target);
  const branch = api.json('').default_branch;
  if (typeof branch !== 'string' || !branch || /[\r\n\0]/.test(branch)) throw Error('Invalid default branch.');
  const readmePath = `contents/README.md?ref=${encodeURIComponent(branch)}`;
  const original = api.json(readmePath);
  const remoteReadme = decodeReadme(original);
  const readme = replaceDownloads(publish ? remoteReadme : (localReadme ?? remoteReadme), table, { readme: true });
  const releaseNotes = target ? replaceDownloads(target.body ?? '', table) : null;
  if (publish) {
    if (otherPublishers(api, currentRunId)) return { deferred: true };
    if (target) {
      if (stableInventory(api)[0]?.id !== target.id) throw Error('Newest stable release changed; retry refresh.');
      const fresh = api.json(`releases/${target.id}`);
      checkTarget(fresh);
      // Not atomic: GitHub release PATCH has no documented body CAS. Writers
      // must serialize; manual note edits must not overlap this GET/PATCH gap.
      if (fresh.body !== target.body) throw Error('Release changed; retry refresh.');
      if (releaseNotes !== (target.body ?? '')) api.write(`releases/${target.id}`, 'PATCH', { body: releaseNotes });
      const verified = api.json(`releases/${target.id}`);
      checkTarget(verified);
      if (verified.body !== releaseNotes || verified.tag_name !== target.tag_name) throw Error('Release readback mismatch.');
    }
    if (readme !== remoteReadme) api.write('contents/README.md', 'PUT', { message: 'docs: refresh current platform downloads', branch, sha: original.sha, content: Buffer.from(readme).toString('base64') });
    if (decodeReadme(api.json(readmePath)) !== readme) throw Error('README readback mismatch.');
  }
  return { inventory, table, readme, releaseNotes, tag: target?.tag_name ?? null, published: publish };
}
function decodeReadme(value) {
  if (value?.encoding !== 'base64' || typeof value.content !== 'string' || !/^[a-f0-9]{40}$/.test(value.sha)) throw Error('Invalid README content.');
  const canonical = value.content.replaceAll('\n', '').replaceAll('\r', '');
  const bytes = Buffer.from(canonical, 'base64'), text = bytes.toString('utf8');
  if (bytes.toString('base64') !== canonical || !Buffer.from(text, 'utf8').equals(bytes)) throw Error('Invalid README base64/UTF-8 content.');
  return text;
}
export function replaceDownloads(text, table, { readme = false } = {}) {
  const start = '<!-- current-downloads:start -->', end = '<!-- current-downloads:end -->';
  const starts = text.split(start).length - 1, ends = text.split(end).length - 1;
  const block = `${start}\n${table}\n${end}`;
  if (starts || ends) {
    if (starts !== 1 || ends !== 1 || text.indexOf(end) < text.indexOf(start)) throw Error('Malformed current-downloads markers.');
    return text.slice(0, text.indexOf(start)) + block + text.slice(text.indexOf(end) + end.length);
  }
  if (readme) {
    const heading = /^# [^\n]+\n/.exec(text);
    if (!heading) throw Error('README must start with a title.');
    return heading[0] + '\n' + block + '\n\n' + text.slice(heading[0].length);
  }
  return block + (text ? '\n\n' + text : '\n');
}
export function renderDownloads({ android, windows }) {
  return [
    '## Current downloads', '',
    'Choose your platform. Each platform keeps its newest verified manifest-backed publication; versions may differ.', '',
    '| Platform | Version | Download | Status |', '| --- | --- | --- | --- |',
    windows ? `| Windows x64 | ${windows.version} | [Installer (.exe)](${windows.url}) · [SHA-256](${windows.checksum}) | Unsigned; Windows may show an unknown-publisher/SmartScreen warning. |` : '| Windows x64 | — | Not yet published | Unsigned installer when available. |',
    android ? `| Android | ${android.version} | [Service APK](${android.service}) · [Browser APK](${android.browser}) · [SHA-256](${android.checksum}) | Install Service first, then Browser. Signed with the Storage release key. |` : '| Android | — | Not yet published | Install Service first, then Browser when available. |', '',
    'Verify SHA-256 checksums before installation. Unchanged platforms retain their original release, version and source identity. These downloads are not an in-app update feed.',
  ].join('\n');
}

export function main(argv = process.argv.slice(2), { run = spawnSync, output = text => console.log(text) } = {}) {
  const options = {};
  for (let i = 0; i < argv.length; i++) {
    const key = argv[i];
    if (!['--repository', '--output-directory', '--publish'].includes(key) || Object.hasOwn(options, key)) throw Error('Usage: current-downloads.mjs --repository OWNER/REPO [--output-directory PATH] [--publish]');
    if (key === '--publish') options[key] = true;
    else {
      const value = argv[++i];
      if (!value || value.startsWith('-') || /[\r\n\0]/.test(value)) throw Error(`Invalid value for ${key}.`);
      options[key] = value;
    }
  }
  const repository = options['--repository']; validateRepository(repository);
  if (!options['--publish'] && !options['--output-directory']) throw Error('Preview requires --output-directory.');
  const api = githubClient(repository, { run });
  // The existing client owns bounded reads and full manifest verification.
  api.write = (path, method, data) => {
    const result = run('gh', ['api', `repos/${repository}/${path}`, '--method', method, '--input', '-'], { encoding: 'utf8', shell: false, timeout: 120_000, maxBuffer: 4 * 1024 * 1024, input: JSON.stringify(data) });
    if (result.error || result.status !== 0) throw Error(`GitHub write failed: ${result.error?.message || result.stderr || result.status}`);
  };
  const result = refreshDownloads({ repository, api, publish: Boolean(options['--publish']), localReadme: options['--publish'] ? undefined : readFileSync(new URL('../README.md', import.meta.url), 'utf8') });
  if (!result.deferred && options['--output-directory']) {
    const directory = resolve(options['--output-directory']); mkdirSync(directory, { recursive: true });
    writeFileSync(join(directory, 'README.md'), result.readme);
    writeFileSync(join(directory, 'release-notes.md'), result.releaseNotes ?? '');
    writeFileSync(join(directory, 'downloads.json'), JSON.stringify(result.inventory, null, 2) + '\n');
  }
  output(JSON.stringify(result.deferred ? result : { tag: result.tag, published: result.published, inventory: result.inventory }, null, 2));
  return result;
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
