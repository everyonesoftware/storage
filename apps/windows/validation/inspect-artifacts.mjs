import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const windows = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const log = fs.readFileSync(path.join(windows, 'validation/logic-transport.txt'), 'utf8');
const assertions = log.split(/\r?\n/).filter(line => line.startsWith('PASS ')).length;
if (assertions === 0 || log.includes('SKIP') || log.includes('FAIL')) throw new Error('Complete passing native test evidence required.');
console.log(`ASSERTIONS=${assertions}; no skips or failures`);
if (process.argv[2]) {
  const library = fs.readFileSync(process.argv[2]);
  console.log(`NATIVE_SHA256=${crypto.createHash('sha256').update(library).digest('hex')}`);
}
for (const relative of [
  'Storage.Service/bin/Debug/net10.0/win-x64/Storage.Service.exe',
  'Storage.Browser/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Storage.Browser.dll'
]) {
  const bytes = fs.readFileSync(path.join(windows, relative));
  const offset = bytes.readUInt32LE(0x3c);
  if (bytes.readUInt16LE(0) !== 0x5a4d || bytes.readUInt32LE(offset) !== 0x4550 || bytes.readUInt16LE(offset + 4) !== 0x8664) throw new Error(`Invalid x64 PE: ${relative}`);
  if (relative.endsWith('Browser.dll') && !bytes.includes(Buffer.from('Storage.Browser.Shell.xaml'))) throw new Error('Embedded runtime shell missing.');
  console.log(`VERIFIED_X64_PE ${relative} (${bytes.length} bytes)`);
}
console.log('PE/resource presence checks only; no native Windows launch or resource resolution claimed.');
