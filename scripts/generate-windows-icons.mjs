// Render the approved Android folder/engine artwork into Windows assets, without image dependencies.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const root = fileURLToPath(new URL('../', import.meta.url));
const args = process.argv.slice(2);
const options = new Map();
for (let i = 0; i < args.length; i += 2) {
    if (!['--application', '--output-directory'].includes(args[i]) || !args[i + 1] || args[i + 1].startsWith('--') || options.has(args[i])) throw Error('Usage: generate-windows-icons.mjs [--application browser|service] [--output-directory PATH]');
    options.set(args[i], args[i + 1]);
}
const application = options.get('--application') ?? 'browser';
if (!['browser', 'service'].includes(application)) throw Error('Application must be browser or service.');
const output = resolve(options.get('--output-directory') ?? resolve(root, `apps/windows/Storage.${application === 'browser' ? 'Browser' : 'Service'}/Assets`));
const vector = readFileSync(resolve(root, `apps/android/${application}/src/main/res/drawable/ic_storage.xml`), 'utf8');
if (!/android:viewportWidth="48"/.test(vector) || !/android:viewportHeight="48"/.test(vector)) throw Error('Unsupported Storage icon viewport.');
// This is a narrow renderer, not an XML/VectorDrawable engine. Reject any
// semantics (groups, alpha, strokes, etc.) that inkAt cannot reproduce.
const structure = vector.replace(/<!--[^]*?-->/g, '').replace(/<path\s+([^>]+)\/>/g, (_, attributes) => {
    const rest = attributes.replace(/android:(?:pathData|fillColor)="[^"]*"/g, '').trim();
    if (rest) throw Error('Unsupported Storage icon path attributes.');
    return '';
}).replace(/<vector\s+([^>]+)>/, (_, attributes) => {
    const rest = attributes.replace(/(?:xmlns:android|android:(?:width|height|viewportWidth|viewportHeight))="[^"]*"/g, '').trim();
    if (rest) throw Error('Unsupported Storage icon vector attributes.');
    return '';
}).replace('</vector>', '').trim();
if (structure) throw Error('Unsupported Storage icon structure.');
const paths = [...vector.matchAll(/<path\s+([^>]+)\/>/g)].map(([, attributes]) => ({
    data: attributes.match(/android:pathData="([^"]+)"/)?.[1],
    color: attributes.match(/android:fillColor="(#[a-fA-F0-9]{6})"/)?.[1],
}));
// Deliberately support only the approved rounded tile and role-specific glyphs.
// Refuse a redesign rather than quietly emitting stale or approximated artwork.
const expected = application === 'browser' ? [
    'M8,4h32a4,4 0,0 1,4 4v32a4,4 0,0 1,-4 4H8a4,4 0,0 1,-4 -4V8a4,4 0,0 1,4 -4',
    'M12,14h10l4,4h10v17H12z',
    'M17,24h14v3H17zM17,29h9v2H17z',
] : [
    'M8,4h32a4,4 0,0 1,4 4v32a4,4 0,0 1,-4 4H8a4,4 0,0 1,-4 -4V8a4,4 0,0 1,4 -4',
    'M14,13h20a2,2 0,0 1,2 2v6a2,2 0,0 1,-2 2H14a2,2 0,0 1,-2 -2v-6a2,2 0,0 1,2 -2M14,26h20a2,2 0,0 1,2 2v6a2,2 0,0 1,-2 2H14a2,2 0,0 1,-2 -2v-6a2,2 0,0 1,2 -2',
    'M16,17h8v2h-8zM16,30h8v2h-8zM32.5,18a1.5,1.5 0,1 0,-3 0a1.5,1.5 0,1 0,3 0M32.5,31a1.5,1.5 0,1 0,-3 0a1.5,1.5 0,1 0,3 0',
];
if (paths.length !== expected.length || paths.some((p, i) => p.data !== expected[i] || !p.color)) throw Error('Unsupported Storage icon geometry/colors; update the renderer for the new artwork.');
const colors = paths.map(p => [1, 3, 5].map(offset => parseInt(p.color.slice(offset, offset + 2), 16)));
function inkAt(x, y) {
    if (x < 4 || x >= 44 || y < 4 || y >= 44) return null;
    const dx = Math.max(8 - x, 0, x - 40), dy = Math.max(8 - y, 0, y - 40);
    if (dx * dx + dy * dy > 16) return null;
    if (application === 'service') {
        for (const top of [13, 26]) {
            if (x < 12 || x >= 36 || y < top || y >= top + 10) continue;
            const cornerX = Math.max(14 - x, 0, x - 34), cornerY = Math.max(top + 2 - y, 0, y - top - 8);
            if (cornerX * cornerX + cornerY * cornerY > 4) continue;
            const centerY = top + 5;
            if ((x >= 16 && x < 24 && y >= centerY - 1 && y < centerY + 1) || (x - 31) ** 2 + (y - centerY) ** 2 <= 2.25) return colors[2];
            return colors[1];
        }
        return colors[0];
    }
    if (x >= 17 && ((x < 31 && y >= 24 && y < 27) || (x < 26 && y >= 29 && y < 31))) return colors[2];
    if (x >= 12 && x < 36 && y >= 14 && y < 35 && (y >= 18 || x <= 22 + y - 14)) return colors[1];
    return colors[0];
}
function crc32(bytes) {
    let crc = 0xffffffff;
    for (const byte of bytes) {
        crc ^= byte;
        for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
    }
    return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
    const bytes = Buffer.alloc(data.length + 12);
    bytes.writeUInt32BE(data.length); bytes.write(type, 4); data.copy(bytes, 8);
    bytes.writeUInt32BE(crc32(bytes.subarray(4, -4)), bytes.length - 4);
    return bytes;
}
function png(size) {
    const raw = Buffer.alloc(size * (1 + size * 4)), samples = 8;
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        const sum = [0, 0, 0]; let covered = 0;
        for (let sy = 0; sy < samples; sy++) for (let sx = 0; sx < samples; sx++) {
            const color = inkAt((x + (sx + .5) / samples) * 48 / size, (y + (sy + .5) / samples) * 48 / size);
            if (color) { covered++; for (let c = 0; c < 3; c++) sum[c] += color[c]; }
        }
        const offset = y * (1 + size * 4) + 1 + x * 4;
        if (covered) for (let c = 0; c < 3; c++) raw[offset + c] = Math.round(sum[c] / covered);
        raw[offset + 3] = Math.round(255 * covered / (samples * samples));
    }
    const header = Buffer.alloc(13);
    header.writeUInt32BE(size); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6;
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256], images = sizes.map(png);
const directory = Buffer.alloc(6 + sizes.length * 16);
directory.writeUInt16LE(1, 2); directory.writeUInt16LE(sizes.length, 4);
let position = directory.length;
for (const [index, size] of sizes.entries()) {
    const offset = 6 + index * 16;
    directory[offset] = directory[offset + 1] = size % 256;
    directory.writeUInt16LE(1, offset + 4); directory.writeUInt16LE(32, offset + 6);
    directory.writeUInt32LE(images[index].length, offset + 8); directory.writeUInt32LE(position, offset + 12);
    position += images[index].length;
}
mkdirSync(output, { recursive: true });
writeFileSync(resolve(output, 'Storage.ico'), Buffer.concat([directory, ...images]));
if (application === 'browser') writeFileSync(resolve(output, 'Storage.png'), images.at(-1));
console.log(`Generated Storage ${application === 'browser' ? 'folder' : 'engine'} branding: ${sizes.join(', ')}px ICO in ${output}`);
