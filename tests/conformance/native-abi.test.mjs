import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { mkdtempSync, writeFileSync, readFileSync, mkdirSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';

const driver = resolve('artifacts/conformance/abi-driver');
const scratch = process.env.TMPDIR || '/opt/data/cache/scratch';
const owner = { principalId: 'local-owner', capabilities: ['readItems', 'createItems', 'createVersions', 'modifyItems', 'manageAccess', 'manageCollections', 'executeQueries', 'readDiagnostics'] };
const reader = { principalId: 'reader', capabilities: ['readItems'] };
const restricted = { principalId: 'local-owner', capabilities: ['readItems'] };

function fixture(t) {
  const work = mkdtempSync(join(scratch, 'storage-abi-'));
  const root = join(work, 'repository');
  mkdirSync(root);
  t.after(() => rmSync(work, { recursive: true, force: true }));
  function invoke(op, args = {}, actor = owner) {
    return JSON.parse(execFileSync(driver, [root, JSON.stringify(actor), JSON.stringify({ op, args })], { encoding: 'utf8' }));
  }
  function ok(op, args, actor) {
    const envelope = invoke(op, args, actor);
    assert.equal(envelope.ok, true, JSON.stringify(envelope));
    assert.ok(Object.hasOwn(envelope, 'result'));
    return envelope.result;
  }
  function denied(op, args, actor, code = 'PermissionDenied') {
    const envelope = invoke(op, args, actor);
    assert.equal(envelope.ok, false, JSON.stringify(envelope));
    assert.equal(envelope.error.code, code);
  }
  function source(name, bytes) {
    const path = join(work, name);
    writeFileSync(path, bytes);
    return path;
  }
  return { work, root, invoke, ok, denied, source };
}

function checkItem(item, name, bytes) {
  for (const key of ['id', 'name', 'contentType', 'revision', 'currentVersionId', 'accessPolicyId', 'accessPolicyRevision', 'createdAt', 'modifiedAt', 'attributes', 'properties']) assert.ok(Object.hasOwn(item, key), `Item missing ${key}`);
  assert.equal(item.name, name);
  assert.equal(item.attributes.byteLength, bytes.length);
  assert.equal(item.attributes.sha256, createHash('sha256').update(bytes).digest('hex'));
  assert.equal(Number.isNaN(Date.parse(item.createdAt)), false);
  assert.equal(Number.isNaN(Date.parse(item.modifiedAt)), false);
}

test('C ABI imports arbitrary binary content, verifies bytes and persists stable identities across calls', t => {
  const f = fixture(t);
  const bytes = Buffer.concat([Buffer.from([0, 255, 128, 13, 10]), randomBytes(1024 * 1024)]);
  const before = f.ok('health');
  const item = f.ok('createItem', { name: 'Arbitrary content.bin', contentType: 'application/octet-stream', sourcePath: f.source('input.bin', bytes) });
  checkItem(item, 'Arbitrary content.bin', bytes);
  const listing = f.ok('listItems');
  assert.equal(listing.items.length, 1);
  assert.equal(listing.items[0].id, item.id);
  const read = f.ok('readVersion', { versionId: item.currentVersionId });
  assert.deepEqual(readFileSync(read.path), bytes);
  const after = f.ok('health');
  assert.equal(after.deviceId, before.deviceId);
  assert.equal(after.dataStoreId, before.dataStoreId);
  assert.equal(after.itemCount, 1);
  assert.equal(after.versionCount, 1);
});

test('new version preserves historical bytes and rejects a stale Item revision', t => {
  const f = fixture(t);
  const original = Buffer.from('original version\n');
  const replacement = Buffer.from('replacement version\n');
  const first = f.ok('createItem', { name: 'Notes.txt', contentType: 'text/plain', sourcePath: f.source('v1.txt', original) });
  const next = f.ok('createVersion', { itemId: first.id, expectedRevision: first.revision, sourcePath: f.source('v2.txt', replacement) });
  assert.notEqual(first.currentVersionId, next.currentVersionId);
  assert.equal(next.revision, first.revision + 1);
  assert.equal(f.ok('listVersions', { itemId: first.id }).versions.length, 2);
  assert.deepEqual(readFileSync(f.ok('readVersion', { versionId: first.currentVersionId }).path), original);
  assert.deepEqual(readFileSync(f.ok('readVersion', { versionId: next.currentVersionId }).path), replacement);
  f.denied('renameItem', { itemId: first.id, expectedRevision: first.revision, name: 'Must not overwrite.txt' }, owner, 'RevisionConflict');
  assert.equal(f.ok('getItem', { itemId: first.id }).name, 'Notes.txt');
});

test('capabilities and current independent policy govern historical version access', t => {
  const f = fixture(t);
  const src = f.source('private.txt', 'private bytes');
  f.denied('createItem', { name: 'Denied.txt', contentType: 'text/plain', sourcePath: src }, restricted);
  const item = f.ok('createItem', { name: 'Private.txt', contentType: 'text/plain', sourcePath: src });
  f.denied('readVersion', { versionId: item.currentVersionId }, reader);
  const policy = f.ok('setAccessPolicy', { itemId: item.id, expectedRevision: item.accessPolicyRevision, readers: ['reader'] });
  assert.equal(policy.id, item.accessPolicyId);
  assert.equal(policy.revision, item.accessPolicyRevision + 1);
  f.ok('readVersion', { versionId: item.currentVersionId }, reader);
  f.ok('setAccessPolicy', { itemId: item.id, expectedRevision: policy.revision, readers: [] });
  f.denied('readVersion', { versionId: item.currentVersionId }, reader);
  const current = f.ok('getItem', { itemId: item.id });
  assert.equal(current.revision, item.revision);
  assert.equal(current.currentVersionId, item.currentVersionId);
  assert.equal(f.ok('listVersions', { itemId: item.id }).versions.length, 1);
});

test('collection deletion removes organization, never the underlying Item', t => {
  const f = fixture(t);
  const item = f.ok('createItem', { name: 'Shared.txt', contentType: 'text/plain', sourcePath: f.source('shared.txt', 'shared content') });
  const c = f.ok('createCollection', { name: 'Reference' });
  const changed = f.ok('addToCollection', { collectionId: c.id, itemId: item.id, expectedRevision: c.revision });
  assert.equal(f.ok('listCollectionItems', { collectionId: c.id }).items[0].id, item.id);
  f.ok('deleteCollection', { collectionId: c.id, expectedRevision: changed.revision });
  assert.equal(f.ok('getItem', { itemId: item.id }).id, item.id);
  assert.equal(f.ok('listItems').items.length, 1);
});

test('missing source and tampered published content fail closed', t => {
  const f = fixture(t);
  const failed = f.invoke('createItem', { name: 'Missing.bin', contentType: 'application/octet-stream', sourcePath: join(f.work, 'does-not-exist') });
  assert.equal(failed.ok, false);
  assert.equal(f.ok('listItems').items.length, 0);
  const item = f.ok('createItem', { name: 'Integrity.txt', contentType: 'text/plain', sourcePath: f.source('integrity.txt', 'verified bytes') });
  const read = f.ok('readVersion', { versionId: item.currentVersionId });
  writeFileSync(read.path, 'corrupt bytes');
  f.denied('readVersion', { versionId: item.currentVersionId }, owner, 'IntegrityError');
});
