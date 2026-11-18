#!/usr/bin/env node
// Inspect the actual signed APKs; no filenames or Gradle metadata are trusted.
import { execFileSync } from 'node:child_process';
import { constants, copyFileSync, existsSync, lstatSync, mkdirSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
function sdkTools() {
  const sdk = process.env.ANDROID_HOME || process.env.ANDROID_SDK_ROOT;
  if (!sdk) throw new Error('Set ANDROID_HOME or ANDROID_SDK_ROOT for APK verification tools');
  // Match the workflow pin: emulator setup may install newer build-tools whose
  // certificate report format differs. Never silently select or fall back to it.
  const apksigner = join(sdk, 'build-tools', '36.0.0', 'apksigner');
  if (!existsSync(apksigner)) throw new Error('Android SDK build-tools 36.0.0 apksigner is required');
  const commands = join(sdk, 'cmdline-tools');
  const candidates = existsSync(commands) ? readdirSync(commands).sort((a, b) => b.localeCompare(a, undefined, { numeric: true })) : [];
  const apkanalyzer = [join(commands, 'latest', 'bin', 'apkanalyzer'), ...candidates.map(v => join(commands, v, 'bin', 'apkanalyzer')), join(sdk, 'tools', 'bin', 'apkanalyzer')].find(existsSync);
  if (!apksigner || !apkanalyzer) throw new Error('Android SDK apksigner and apkanalyzer are required');
  return { apksigner, apkanalyzer };
}
const execute = (tool, args) => execFileSync(tool, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 16 * 1024 * 1024 });

export async function packageAndroid({ root = repository, version, directory, run = execute, tools } = {}) {
  const match = /^0\.1\.([1-9]\d*)$/.exec(version || '');
  if (!match || Number(match[1]) > 65534) throw new Error('Release version must be 0.1.N with N in 1..65534');
  if (!directory) throw new Error('An output directory is required');
  tools ??= sdkTools();
  const products = [];
  for (const app of ['service', 'browser']) {
    const source = join(root, `apps/android/${app}/build/outputs/apk/release/${app}-release.apk`);
    if (!lstatSync(source).isFile() || lstatSync(source).isSymbolicLink()) throw new Error(`Expected a regular signed ${app} release APK`);
    const certificate = run(tools.apksigner, ['verify', '--verbose', '--print-certs', source]);
    // Accept only the pinned tool's single-signer report. Count all certificate
    // identity records first so an unrecognized extra signer cannot be ignored.
    const records = certificate.split(/\r?\n/).filter(line => /certificate (?:DN|SHA-256 digest):/.test(line));
    const identities = records.filter(line => /^Signer #1 certificate DN: \S.*$/.test(line));
    const digests = [...certificate.matchAll(/^Signer #1 certificate SHA-256 digest: ([a-fA-F0-9]{64})[ \t]*\r?$/gm)];
    const counts = certificate.split(/\r?\n/).filter(line => line.startsWith('Number of signers:'));
    if (records.length !== 2 || identities.length !== 1 || digests.length !== 1 || counts.some(line => line !== 'Number of signers: 1') || /(?:CN\s*=\s*Android Debug|Android Debug)/i.test(certificate)) throw new Error(`${app}: one non-debug release signer is required`);
    const signer = digests[0][1].toLowerCase();
    if (products.length && signer !== products[0].signer) throw new Error('Service and Browser release signers differ');
    const inspect = field => run(tools.apkanalyzer, ['manifest', field, source]).trim();
    if (inspect('application-id') !== `com.everyonesoftware.storage.${app}`) throw new Error(`${app}: wrong application ID`);
    if (inspect('version-name') !== version || inspect('version-code') !== match[1]) throw new Error(`${app}: wrong APK release version`);
    const entries = run(tools.apkanalyzer, ['files', 'list', source]).split(/\r?\n/).map(s => s.trim().replace(/^\//, ''));
    const native = entries.filter(s => /(?:^|\/)libstorage_(?:core|descriptor_io)\.so$/.test(s)).sort();
    const expected = app === 'service' ? ['arm64-v8a', 'x86_64'].flatMap(abi => [`lib/${abi}/libstorage_core.so`, `lib/${abi}/libstorage_descriptor_io.so`]).sort() : [];
    if (JSON.stringify(native) !== JSON.stringify(expected)) throw new Error(`${app}: incorrect service-private native ABI inventory`);
    const name = `Storage-${app === 'service' ? 'Service' : 'Browser'}-${version}.apk`;
    products.push({ source, signer, target: resolve(directory, name) });
  }
  // Validate the complete pair before writing any publishable-looking output.
  if (existsSync(directory) && (!lstatSync(directory).isDirectory() || lstatSync(directory).isSymbolicLink())) throw new Error('Output must be a real directory');
  for (const p of products) if (existsSync(p.target)) throw new Error(`Refusing to overwrite ${p.target}`);
  mkdirSync(directory, { recursive: true });
  for (const p of products) copyFileSync(p.source, p.target, constants.COPYFILE_EXCL);
  return products.map(p => p.target);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    if (args.length !== 4 || args[0] !== '--version' || args[2] !== '--directory') throw new Error('Usage: node scripts/package-android.mjs --version 0.1.N --directory dist/android');
    for (const output of await packageAndroid({ version: args[1], directory: args[3] })) console.log(output);
  } catch (error) { console.error(`Android packaging failed: ${error.message}`); process.exitCode = 1; }
}
