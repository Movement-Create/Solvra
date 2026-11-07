// Checkpoint store: review, keep, undo, restore (compiled to out/ by `npm test`).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { CheckpointStore } = require('../out/checkpoints');
const { agentPath, extraDirs, buildPrompt } = require('../out/prompt');
const { buildStreamArgs } = require('../out/stream-run');

function setup() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'svckpt-'));
  const store = new CheckpointStore(path.join(dir, '.ckpt'));
  // What the backend does: copy the original, then edit.
  const edit = (id, file, content, at) => {
    const abs = path.join(dir, file);
    let before = null;
    if (fs.existsSync(abs)) { before = path.join(store.dir, id + '.orig'); fs.mkdirSync(store.dir, { recursive: true }); fs.copyFileSync(abs, before); }
    fs.writeFileSync(abs, content);
    store.record(id, 's1', abs, before, at);
    return abs;
  };
  return { dir, store, edit };
}

test('undo restores the original; a new file is deleted', () => {
  const { dir, store, edit } = setup();
  const a = path.join(dir, 'a.txt');
  fs.writeFileSync(a, 'v0');
  edit('e1', 'a.txt', 'v1', 1);
  const created = edit('e2', 'new.txt', 'hello', 2);
  assert.equal(store.original('e1'), 'v0');
  assert.equal(store.original('e2'), '');
  const r = store.undo(['e1', 'e2']);
  assert.deepEqual(r.undone.sort(), ['e1', 'e2']);
  assert.equal(fs.readFileSync(a, 'utf8'), 'v0');
  assert.ok(!fs.existsSync(created));
  assert.equal(store.get('e1').state, 'undone');
});

test('several edits to one file unwind newest first (restore to before a message)', () => {
  const { dir, store, edit } = setup();
  fs.writeFileSync(path.join(dir, 'a.txt'), 'v0');
  edit('e1', 'a.txt', 'v1', 1);
  edit('e2', 'a.txt', 'v2', 2);
  const r = store.undo(['e1', 'e2']);
  assert.deepEqual(r.conflicts, []);
  assert.equal(fs.readFileSync(path.join(dir, 'a.txt'), 'utf8'), 'v0');
});

test('a file changed after the edit is a conflict unless forced', () => {
  const { dir, store, edit } = setup();
  const a = path.join(dir, 'a.txt');
  fs.writeFileSync(a, 'v0');
  edit('e1', 'a.txt', 'v1', 1);
  edit('e2', 'a.txt', 'v2', 2);
  // Undoing only the older edit would discard the newer one.
  let r = store.undo(['e1']);
  assert.equal(r.conflicts.length, 1);
  assert.equal(fs.readFileSync(a, 'utf8'), 'v2');
  // The user edits the file by hand.
  fs.writeFileSync(a, 'mine');
  r = store.undo(['e2']);
  assert.deepEqual(r.conflicts.map((c) => c.toolId), ['e2']);
  r = store.undo(['e2'], true);
  assert.deepEqual(r.undone, ['e2']);
  assert.equal(fs.readFileSync(a, 'utf8'), 'v1');
});

test('keep marks the edit; the index survives a reload; per-session view', () => {
  const { dir, store, edit } = setup();
  fs.writeFileSync(path.join(dir, 'a.txt'), 'v0');
  edit('e1', 'a.txt', 'v1', 1);
  assert.ok(store.keep('e1'));
  const again = new CheckpointStore(store.dir);
  assert.equal(again.get('e1').state, 'kept');
  assert.deepEqual(Object.keys(again.forSession('s1')), ['e1']);
  assert.deepEqual(again.forSession('other'), {});
  assert.equal(again.keep('missing'), false);
});

test('folders: other roots and @ folders outside the working folder become --add-dir', () => {
  const chips = [
    { kind: 'folder', path: 'src', abs: '/w/app/src', label: 'src/' },
    { kind: 'folder', path: '/data/sets', abs: '/data/sets', label: 'sets/' },
    { kind: 'file', path: 'x.ts', abs: '/w/app/x.ts', label: 'x.ts' },
  ];
  assert.deepEqual(extraDirs(chips, '/w/app', ['/w/app', '/w/lib']).sort(), ['/data/sets', '/w/lib']);
  assert.equal(agentPath(chips[0], '/w/app'), 'src');
  assert.equal(agentPath(chips[1], '/w/app'), '/data/sets');
  assert.match(buildPrompt('look', chips, '/w/app'), /Folders the user attached .*: src, \/data\/sets/);
  const args = buildStreamArgs({ prefix: ['S.dll'], prompt: 'p', model: 'm', mode: 'ask', addDirs: ['/w/lib'], checkpointDir: '/tmp/c' });
  assert.equal(args[args.indexOf('--add-dir') + 1], '/w/lib');
  assert.equal(args[args.indexOf('--checkpoint-dir') + 1], '/tmp/c');
});
