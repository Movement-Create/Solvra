// A run shared between views (sidebar + editor tabs). Compiled to out/ by `npm test`.
const test = require('node:test');
const assert = require('node:assert/strict');
const { ActiveRun, withoutLiveTurn } = require('../out/live-runs');

function viewer() {
  const v = { got: [], ended: 0, post: (m) => v.got.push(m), runEnded: () => { v.ended++; }, adoptSession: () => {} };
  return v;
}

test('a view that attaches late gets the turn so far, then live events', () => {
  const run = new ActiveRun('r1', 'prompt', { type: 'runStarted', runId: 'r1', text: 'hi', chips: [] }, 's1');
  const sidebar = viewer();
  run.attach(sidebar);
  run.emit({ type: 'stream', runId: 'r1', event: { type: 'text_delta', agent: 'main', text: 'a' } });
  const tab = viewer();
  run.attach(tab);
  run.attach(tab); // attaching twice replays nothing more
  run.emit({ type: 'stream', runId: 'r1', event: { type: 'text_delta', agent: 'main', text: 'b' } });
  assert.deepEqual(tab.got.map((m) => m.type), ['runStarted', 'stream', 'stream']);
  assert.deepEqual(sidebar.got.map((m) => m.type), ['runStarted', 'stream', 'stream']);
});

test('the run is cancelled only when the last view leaves', () => {
  const run = new ActiveRun('r1', 'p', { type: 'runStarted', runId: 'r1', text: '', chips: [] }, 's1');
  let cancelled = 0;
  run.run = { cancel: () => { cancelled++; }, respond: () => {} };
  const a = viewer(), b = viewer();
  run.attach(a); run.attach(b);
  run.detach(a);
  assert.equal(cancelled, 0);
  run.detach(a); // not attached any more: no effect
  run.detach(b);
  assert.equal(cancelled, 1);
});

test('withoutLiveTurn drops the in-progress turn only when it is the run prompt', () => {
  const m = [
    { role: 'user', text: 'first', tools: [] }, { role: 'assistant', text: 'ok', tools: [] },
    { role: 'user', text: 'run 3 subagents', tools: [] }, { role: 'assistant', text: '', tools: [{ id: 't', name: 'agent', input: {}, output: null, ok: null }] },
  ];
  assert.deepEqual(withoutLiveTurn(m, ' run 3 subagents ').map((x) => x.text), ['first', 'ok']);
  assert.equal(withoutLiveTurn(m, 'something else'), m); // prompt not logged yet: keep everything
  assert.deepEqual(withoutLiveTurn([], 'x'), []);
});
