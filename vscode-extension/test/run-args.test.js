// Unit tests for src/run-args.ts. Run with `npm test` (compiles to out/ first).
const test = require('node:test');
const assert = require('node:assert/strict');
const {
  buildRunArgs, normalizeMode, permissionArgs, parseApprovalNeeded, APPROVAL_NEEDED_MARKER,
} = require('../out/run-args');

const base = { dll: 'Solvra.dll', prompt: 'edit sample.js', provider: 'chatgpt', model: 'gpt-6.1-sol', maxTurns: 0 };

test('ask mode never passes --auto and pins the default permission mode', () => {
  const args = buildRunArgs({ ...base, mode: 'ask' });
  assert.ok(!args.includes('--auto'), args.join(' '));
  assert.ok(!args.includes('--plan'));
  const i = args.indexOf('--permission-mode');
  assert.equal(args[i + 1], 'default');
});

test('plan mode is read-only and never paired with --auto', () => {
  const args = buildRunArgs({ ...base, mode: 'plan' });
  assert.ok(!args.includes('--auto'), args.join(' '));
  assert.ok(args.includes('--plan'));
  assert.equal(args[args.indexOf('--permission-mode') + 1], 'plan');
});

test('auto mode passes --auto', () => {
  const args = buildRunArgs({ ...base, mode: 'auto' });
  assert.ok(args.includes('--auto'));
  assert.equal(args[args.indexOf('--permission-mode') + 1], 'auto');
});

test('session id is forwarded', () => {
  const args = buildRunArgs({ ...base, mode: 'ask', sessionId: 'abc' });
  assert.deepEqual(args.slice(-2), ['--session', 'abc']);
});

test('unknown and legacy modes fall back to ask unless a fallback is given', () => {
  for (const v of ['agent', 'edit', '', undefined, null, 42]) assert.equal(normalizeMode(v), 'ask');
  assert.equal(normalizeMode('AUTO'), 'auto');
  assert.equal(normalizeMode(' plan '), 'plan');
  assert.equal(normalizeMode('default'), 'ask');
  assert.equal(normalizeMode(undefined, 'auto'), 'auto');
  assert.equal(normalizeMode('agent', 'auto'), 'auto');
});

test('every mode maps to explicit permission flags', () => {
  for (const m of ['ask', 'plan', 'auto']) assert.ok(permissionArgs(m).includes('--permission-mode'));
});

test('approval-needed markers are parsed from stderr', () => {
  const stderr = [
    '[warning] No terminal to ask for approval: write/exec/agent tools will be refused. Use --auto to allow them.',
    `${APPROVAL_NEEDED_MARKER} file_edit`,
    'some other line',
    `${APPROVAL_NEEDED_MARKER} bash`,
    `${APPROVAL_NEEDED_MARKER} file_edit`,
  ].join('\r\n');
  assert.deepEqual(parseApprovalNeeded(stderr), ['file_edit', 'bash']);
  assert.deepEqual(parseApprovalNeeded('nothing here'), []);
});
