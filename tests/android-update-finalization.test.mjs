import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const read = name => readFileSync(new URL(`../apps/android/browser/src/main/java/com/everyonesoftware/storage/browser/updates/${name}.kt`, import.meta.url), 'utf8');

test('Controller finalizes through the tested boundary and permits only cleanup retries while pending', () => {
 const source = read('UpdateController');
 assert.match(source, /finally \{ finishUpdateOperation\(gate, mutable, installer::cancel\) \}/);
 assert.match(source, /if \(!gate\.enter\(\)\) return\s+if \(mutable\.value\.cleanupPending && !cleanupRetry\) \{ gate\.leave\(\); return \}/);
 // Idle retry must request cancellation inside the entered operation: enter resets cancellation.
 assert.match(source, /operation\(cleanupRetry = true\) \{ gate\.cancel\(\) \}/);
});

test('Cleanup retry stays visible even when no primary session ID reached UI state', () => {
 const source = read('UpdateSettings');
 assert.match(source, /onCancel = controller::cancel/);
 assert.match(source, /if \(state\.busy \|\| state\.session \|\| state\.cleanupPending\) TextButton\(onClick = onCancel\)/);
 assert.match(source, /!state\.busy && !state\.session && !state\.cleanupPending/);
 assert.match(source, /val offered = state\.release != null && state\.nextPackage != null/);
 assert.match(source, /if \(offered && !state\.ready && !state\.session && !state\.cleanupPending\)/);
 assert.match(source, /if \(state\.ready && state\.nextPackage != null && !state\.session && !state\.cleanupPending\)/);
});
