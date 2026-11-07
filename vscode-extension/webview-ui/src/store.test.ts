import { describe, expect, it } from 'vitest';
import type { StreamEvent } from '../../src/protocol';
import { applyEvent, autoChip, groupSessions, initialState, parseFileRef, reducer, type State } from './store';

const run = (events: StreamEvent[], s: State = initialState): State => {
  let st = reducer({ ...s, ready: true }, { type: 'runStarted', runId: 'r1', text: 'go', chips: [] });
  for (const e of events) st = reducer(st, { type: 'stream', runId: 'r1', event: e });
  return st;
};
const cur = (s: State) => s.turns[s.turns.length - 1];

describe('stream events', () => {
  it('merges text deltas per agent and keeps order with tools', () => {
    const s = run([
      { type: 'text_delta', agent: 'main', text: 'Hel' },
      { type: 'text_delta', agent: 'main', text: 'lo' },
      { type: 'tool_call', agent: 'main', id: 't1', name: 'file_read', input: { path: 'a.js' }, summary: 'Read a.js', kind: 'read' },
      { type: 'text_delta', agent: 'main', text: 'Done' },
    ]);
    expect(cur(s).items.map((i) => i.kind)).toEqual(['text', 'tool', 'text']);
    expect((cur(s).items[0] as { text: string }).text).toBe('Hello');
  });

  it('tracks a permission request on its tool and clears it on the result', () => {
    let s = run([
      { type: 'tool_call', agent: 'main', id: 't1', name: 'file_edit', input: {}, summary: 'Edit a.js', kind: 'edit' },
      { type: 'permission_request', id: 'p1', agent: 'main', tool_call_id: 't1', tool: 'file_edit', kind: 'edit', summary: 'Edit a.js', diff: '@@ -1 +1 @@\n-a\n+b', command: null, always: 'edit' },
    ]);
    expect(s.approvals.map((a) => a.id)).toEqual(['p1']);
    expect(cur(s).items[0]).toMatchObject({ status: 'waiting', diff: '@@ -1 +1 @@\n-a\n+b' });
    s = reducer(s, { type: 'local/answered', id: 'p1', decision: 'allow' });
    expect(s.approvals).toEqual([]);
    expect(cur(s).items[0]).toMatchObject({ status: 'running' });
    s = reducer(s, { type: 'stream', runId: 'r1', event: { type: 'tool_result', agent: 'main', id: 't1', ok: true, output: 'Edited', diff: '@@', meta: { added: 1, removed: 1, lines: 1 } } });
    expect(cur(s).items[0]).toMatchObject({ status: 'ok', meta: { added: 1, removed: 1 } });
  });

  it('marks a denied tool as denied, not failed', () => {
    const s = run([
      { type: 'tool_call', agent: 'main', id: 't1', name: 'bash', input: { command: 'rm x' }, summary: 'Run rm x', kind: 'execute' },
      { type: 'tool_result', agent: 'main', id: 't1', ok: false, output: 'The user denied "bash". Do not retry.', diff: null, meta: { added: null, removed: null, lines: 1 } },
    ]);
    expect(cur(s).items[0]).toMatchObject({ status: 'denied' });
  });

  it('nests parallel subagents with their own steps', () => {
    const s = run([
      { type: 'tool_call', agent: 'main', id: 'ag1', name: 'agent', input: {}, summary: 'Agent: A', kind: 'agent' },
      { type: 'tool_call', agent: 'main', id: 'ag2', name: 'agent', input: {}, summary: 'Agent: B', kind: 'agent' },
      { type: 'agent_start', agent: 'a1', parent: 'main', tool_call_id: 'ag1', title: 'Count A' },
      { type: 'agent_start', agent: 'a2', parent: 'main', tool_call_id: 'ag2', title: 'Count B' },
      { type: 'tool_call', agent: 'a2', id: 'x2', name: 'bash', input: {}, summary: 'Run wc', kind: 'execute' },
      { type: 'tool_call', agent: 'a1', id: 'x1', name: 'bash', input: {}, summary: 'Run wc', kind: 'execute' },
      { type: 'text_delta', agent: 'a1', text: 'two' },
      { type: 'agent_end', agent: 'a2', status: 'ok' },
    ]);
    const t = cur(s);
    expect(Object.keys(t.agents)).toEqual(['a1', 'a2']);
    expect(t.agents.a1).toMatchObject({ toolCallId: 'ag1', status: 'running' });
    expect(t.agents.a2.status).toBe('ok');
    expect(t.items.filter((i) => 'agent' in i && i.agent === 'a1').map((i) => i.kind)).toEqual(['tool', 'text']);
  });

  it('done: summary line values, unfinished steps closed, final text kept when nothing streamed', () => {
    const s = run([
      { type: 'tool_call', agent: 'main', id: 't1', name: 'bash', input: {}, summary: 'Run x', kind: 'execute' },
      { type: 'usage', agent: 'main', input: 1000, output: 200, cost_usd: 0.004, context_pct: 18 },
      { type: 'done', stop_reason: 'cancelled', text: 'partial', error: null, session_id: 's9', turns: 1, duration_ms: 12000, cost_usd: 0.004, usage: { input: 1000, output: 200 } },
    ]);
    const t = cur(s);
    expect(t.status).toBe('cancelled');
    expect(t.summary).toMatchObject({ durationMs: 12000, tokens: 1200, costUsd: 0.004 });
    expect(t.items[0]).toMatchObject({ status: 'denied' });
    expect(t.items[1]).toMatchObject({ kind: 'text', text: 'partial' });
    expect(s.sessionId).toBe('s9');
    expect(s.usage.contextPct).toBe(18);
  });

  it('plan and errors with fixes', () => {
    const s = run([
      { type: 'plan', agent: 'main', items: [{ text: 'Read', status: 'done' }, { text: 'Fix', status: 'active' }] },
      { type: 'error', message: 'No Google key', fix: 'add_api_key', provider: 'google' },
    ]);
    expect(s.plan).toHaveLength(2);
    expect(cur(s).items[0]).toMatchObject({ kind: 'error', fix: 'add_api_key', provider: 'google' });
  });

  it('ignores events from an older run', () => {
    const s = run([]);
    const after = reducer(s, { type: 'stream', runId: 'old', event: { type: 'text_delta', agent: 'main', text: 'x' } });
    expect(after).toBe(s);
  });

  it('applyEvent session sets id and title', () => {
    const s = applyEvent(run([]), { type: 'session', id: 'abc', title: 'Bump a', model: 'm', provider: 'p', cwd: '/', mode: 'ask', effort: 'medium', resumed: false, rules: [] });
    expect(s.sessionId).toBe('abc');
    expect(s.title).toBe('Bump a');
  });
});

describe('sessions, chips and file refs', () => {
  it('groups sessions by day and filters by title', () => {
    const now = new Date('2026-11-07T12:00:00Z');
    const rows = [
      { id: '1', title: 'Fix login', created_at: '', updated_at: '2026-11-07T09:00:00Z', model: '', provider: '' },
      { id: '2', title: 'Refactor', created_at: '', updated_at: '2026-11-06T09:00:00Z', model: '', provider: '' },
      { id: '3', title: 'Old fix', created_at: '', updated_at: '2026-10-01T09:00:00Z', model: '', provider: '' },
    ];
    expect(groupSessions(rows, '', now).map((g) => [g.label, g.rows.length])).toEqual([['Today', 1], ['Yesterday', 1], ['Earlier', 1]]);
    expect(groupSessions(rows, 'fix', now).flatMap((g) => g.rows.map((r) => r.id))).toEqual(['1', '3']);
  });

  it('auto selection chip can be dismissed and is not duplicated', () => {
    const chip = { kind: 'selection' as const, path: 'a.ts', label: 'a.ts:1-2', startLine: 1, endLine: 2, text: 'x' };
    let s = reducer({ ...initialState }, { type: 'selection', chip });
    expect(autoChip(s)).toEqual(chip);
    s = reducer(s, { type: 'local/dismissSelection' });
    expect(autoChip(s)).toBeNull();
    s = reducer({ ...initialState }, { type: 'selection', chip });
    s = reducer(s, { type: 'local/addChip', chip });
    expect(autoChip(s)).toBeNull();
  });

  it('new chat keeps settings and model but clears the transcript', () => {
    let s = run([{ type: 'text_delta', agent: 'main', text: 'hi' }]);
    s = reducer({ ...s, model: 'chatgpt:x', mode: 'auto' }, { type: 'newChat' });
    expect(s.turns).toEqual([]);
    expect(s.model).toBe('chatgpt:x');
    expect(s.mode).toBe('auto');
  });

  it('parses file references, rejects URLs and versions', () => {
    expect(parseFileRef('sample.js:4')).toEqual({ path: 'sample.js', line: 4 });
    expect(parseFileRef('src/app.ts')).toEqual({ path: 'src/app.ts', line: undefined });
    expect(parseFileRef('sample.js')).toEqual({ path: 'sample.js', line: undefined });
    expect(parseFileRef('src/app.ts:12:5')).toEqual({ path: 'src/app.ts', line: 12 });
    expect(parseFileRef('https://x.com/a.js')).toBeNull();
    expect(parseFileRef('1.2.3')).toBeNull();
    expect(parseFileRef('npm install')).toBeNull();
  });
});
