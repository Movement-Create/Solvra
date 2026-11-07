// Chat state and the reducer that applies backend stream events. Pure: unit-tested in store.test.ts.
import type {
  ChatMode, Chip, FixKind, HostMessage, PermissionRequestEvent, PlanItem, ProviderGroup, SessionRow,
  SettingsState, StreamEvent, ToolInfo, ToolKind, ToolResultMeta, TranscriptMessage,
} from '../../src/protocol';

export type ToolStatus = 'running' | 'waiting' | 'ok' | 'error' | 'denied';

export type Item =
  | { kind: 'text'; key: string; agent: string; text: string }
  | { kind: 'thinking'; key: string; agent: string; text: string }
  | {
      kind: 'tool'; key: string; agent: string; id: string; name: string; summary: string; toolKind: ToolKind;
      input: Record<string, unknown>; status: ToolStatus; output?: string; diff?: string | null; meta?: ToolResultMeta;
    }
  | { kind: 'error'; key: string; message: string; fix: FixKind | null; provider?: string | null }
  | { kind: 'notice'; key: string; text: string };

export interface SubAgent { id: string; parent: string; toolCallId: string | null; title: string; status: 'running' | 'ok' | 'error' | 'cancelled'; startedAt: number; endedAt?: number }

export interface Turn {
  id: string;
  user: { text: string; chips: Chip[] };
  items: Item[];
  agents: Record<string, SubAgent>;
  status: 'running' | 'done' | 'error' | 'cancelled';
  startedAt: number;
  summary?: { durationMs: number; tokens: number; costUsd: number; stopReason: string };
}

export type View = 'chat' | 'settings' | 'memory' | 'tools';

export interface State {
  turns: Turn[];
  sessionId: string | null;
  title: string;
  runId: string | null;
  approvals: PermissionRequestEvent[];
  plan: PlanItem[];
  usage: { input: number; output: number; costUsd: number; contextPct: number };
  mode: ChatMode;
  model: string;
  providers: ProviderGroup[];
  sessions: SessionRow[];
  settings: SettingsState | null;
  view: View;
  chips: Chip[];
  selection: Chip | null;
  selectionDismissed: string | null;
  draft: { text: string; send: boolean; nonce: number } | null;
  tools: ToolInfo[] | null;
  memory: { query: string; results: string[]; added?: boolean } | null;
  files: { query: string; items: { path: string; label: string; kind: 'file' | 'folder' }[] };
  hostError: { message: string; fix?: FixKind | null; provider?: string | null } | null;
  openRequest: { view: 'sessions' | 'model'; nonce: number } | null;
  inTab: boolean;
  workspaceName: string | null;
  ready: boolean;
}

export const initialState: State = {
  turns: [], sessionId: null, title: 'New chat', runId: null, approvals: [], plan: [],
  usage: { input: 0, output: 0, costUsd: 0, contextPct: 0 }, mode: 'ask', model: '', providers: [], sessions: [],
  settings: null, view: 'chat', chips: [], selection: null, selectionDismissed: null, draft: null, tools: null, memory: null, files: { query: '', items: [] },
  hostError: null, openRequest: null, inTab: false, workspaceName: null, ready: false,
};

export type Action =
  | HostMessage
  | { type: 'local/setMode'; mode: ChatMode }
  | { type: 'local/setModel'; model: string }
  | { type: 'local/view'; view: View }
  | { type: 'local/removeChip'; index: number }
  | { type: 'local/addChip'; chip: Chip }
  | { type: 'local/clearChips' }
  | { type: 'local/answered'; id: string; decision: 'allow' | 'allow_always' | 'deny' }
  | { type: 'local/dismissHostError' }
  | { type: 'local/dismissSelection' };

export const MODES: ChatMode[] = ['ask', 'plan', 'auto'];
export const nextMode = (m: ChatMode): ChatMode => MODES[(MODES.indexOf(m) + 1) % MODES.length];

let seq = 0;
const key = () => `k${++seq}`;

function mapLast<T>(arr: T[], pred: (t: T) => boolean, fn: (t: T) => T): T[] {
  for (let i = arr.length - 1; i >= 0; i--) if (pred(arr[i])) { const c = arr.slice(); c[i] = fn(arr[i]); return c; }
  return arr;
}

function updateCurrent(state: State, fn: (t: Turn) => Turn): State {
  if (!state.turns.length) return state;
  const turns = state.turns.slice();
  turns[turns.length - 1] = fn(turns[turns.length - 1]);
  return { ...state, turns };
}

function updateTool(t: Turn, id: string, fn: (i: Extract<Item, { kind: 'tool' }>) => Item): Turn {
  return { ...t, items: t.items.map((i) => (i.kind === 'tool' && i.id === id ? fn(i) : i)) };
}

/** Apply one backend event to the current turn. */
export function applyEvent(state: State, ev: StreamEvent, now = Date.now()): State {
  switch (ev.type) {
    case 'session':
      return { ...state, sessionId: ev.id ?? state.sessionId, title: ev.title || state.title };
    case 'text_delta':
    case 'thinking_delta': {
      const kind = ev.type === 'text_delta' ? 'text' : 'thinking';
      return updateCurrent(state, (t) => {
        // Append to this agent's last item when it is the same kind and nothing of that agent came between.
        const lastOfAgent = [...t.items].reverse().find((i) => 'agent' in i && i.agent === ev.agent);
        if (lastOfAgent && lastOfAgent.kind === kind) {
          return { ...t, items: t.items.map((i) => (i === lastOfAgent ? { ...i, text: (i as { text: string }).text + ev.text } as Item : i)) };
        }
        return { ...t, items: [...t.items, { kind, key: key(), agent: ev.agent, text: ev.text } as Item] };
      });
    }
    case 'tool_call':
      return updateCurrent(state, (t) => ({
        ...t,
        items: [...t.items, {
          kind: 'tool', key: key(), agent: ev.agent, id: ev.id, name: ev.name, summary: ev.summary,
          toolKind: ev.kind, input: ev.input ?? {}, status: 'running',
        }],
      }));
    case 'permission_request': {
      const s = updateCurrent(state, (t) => updateTool(t, ev.tool_call_id, (i) => ({ ...i, status: 'waiting', diff: ev.diff ?? i.diff })));
      return { ...s, approvals: [...s.approvals.filter((a) => a.id !== ev.id), ev] };
    }
    case 'permission_rule':
      return state;
    case 'tool_result': {
      const s = updateCurrent(state, (t) => updateTool(t, ev.id, (i) => ({
        ...i,
        status: ev.ok ? 'ok' : /denied|refused|needs approval|read-only/i.test(ev.output) ? 'denied' : 'error',
        output: ev.output, diff: ev.diff ?? i.diff, meta: ev.meta,
      })));
      return { ...s, approvals: s.approvals.filter((a) => a.tool_call_id !== ev.id) };
    }
    case 'agent_start':
      return updateCurrent(state, (t) => ({
        ...t,
        agents: { ...t.agents, [ev.agent]: { id: ev.agent, parent: ev.parent, toolCallId: ev.tool_call_id, title: ev.title, status: 'running', startedAt: now } },
      }));
    case 'agent_end':
      return updateCurrent(state, (t) => {
        const a = t.agents[ev.agent];
        return a ? { ...t, agents: { ...t.agents, [ev.agent]: { ...a, status: ev.status, endedAt: now } } } : t;
      });
    case 'plan':
      return { ...state, plan: ev.items };
    case 'usage':
      return { ...state, usage: { input: ev.input, output: ev.output, costUsd: ev.cost_usd, contextPct: ev.context_pct } };
    case 'error':
      return updateCurrent(state, (t) => ({ ...t, items: [...t.items, { kind: 'error', key: key(), message: ev.message, fix: ev.fix, provider: ev.provider }] }));
    case 'done': {
      const status: Turn['status'] = ev.stop_reason === 'cancelled' ? 'cancelled' : ev.stop_reason === 'error' ? 'error' : 'done';
      const s = updateCurrent(state, (t) => {
        // Tools still marked running/waiting when the run stops did not finish.
        const items = t.items.map((i) => (i.kind === 'tool' && (i.status === 'running' || i.status === 'waiting') ? { ...i, status: 'denied' as const } : i));
        const agents = Object.fromEntries(Object.entries(t.agents).map(([k, a]) => [k, a.status === 'running' ? { ...a, status: 'cancelled' as const, endedAt: now } : a]));
        // Non-streamed final text (e.g. a provider that does not stream) still appears.
        const hasText = items.some((i) => i.kind === 'text' && i.agent === 'main' && i.text.trim());
        const finalItems = !hasText && ev.text.trim() ? [...items, { kind: 'text' as const, key: key(), agent: 'main', text: ev.text }] : items;
        return {
          ...t, items: finalItems, agents, status,
          summary: { durationMs: ev.duration_ms, tokens: ev.usage.input + ev.usage.output, costUsd: ev.cost_usd, stopReason: ev.stop_reason },
        };
      });
      return { ...s, sessionId: ev.session_id ?? s.sessionId, approvals: [] };
    }
  }
}

function fromTranscript(messages: TranscriptMessage[]): Turn[] {
  const turns: Turn[] = [];
  for (const m of messages) {
    if (m.role === 'user') {
      turns.push({ id: key(), user: { text: m.text, chips: [] }, items: [], agents: {}, status: 'done', startedAt: 0 });
      continue;
    }
    if (!turns.length) turns.push({ id: key(), user: { text: '', chips: [] }, items: [], agents: {}, status: 'done', startedAt: 0 });
    const t = turns[turns.length - 1];
    for (const tool of m.tools) {
      t.items.push({
        kind: 'tool', key: key(), agent: 'main', id: tool.id, name: tool.name, summary: summarize(tool.name, tool.input),
        toolKind: 'read', input: tool.input, status: tool.ok === false ? 'error' : 'ok', output: tool.output ?? undefined,
      });
    }
    if (m.text) t.items.push({ kind: 'text', key: key(), agent: 'main', text: m.text });
  }
  return turns;
}

/** Same verbs as the backend's summaries, for transcripts loaded from disk. */
export function summarize(name: string, input: Record<string, unknown>): string {
  const s = (k: string) => (typeof input[k] === 'string' ? (input[k] as string) : '');
  switch (name) {
    case 'file_edit': return `Edit ${s('path')}`;
    case 'file_write': return `Write ${s('path')}`;
    case 'file_read': return `Read ${s('path')}`;
    case 'bash': return `Run ${s('command')}`;
    case 'grep': return `Search ${s('pattern')}`;
    case 'glob': return `Find ${s('pattern')}`;
    case 'web_fetch': return `Fetch ${s('url')}`;
    case 'web_search': return `Search the web for ${s('query')}`;
    case 'agent': return `Agent: ${s('description') || s('prompt').split('\n')[0]}`;
    case 'todo': return 'Update plan';
    default: return name;
  }
}

export function reducer(state: State, a: Action): State {
  switch (a.type) {
    case 'init':
      return {
        ...state, ready: true, mode: a.state.mode, model: a.state.model, settings: a.state.settings,
        providers: a.state.providers, sessions: a.state.sessions, inTab: a.state.inTab, workspaceName: a.state.workspaceName,
      };
    case 'runStarted':
      return {
        ...state, runId: a.runId, hostError: null, approvals: [], chips: [],
        turns: [...state.turns, { id: a.runId, user: { text: a.text, chips: a.chips }, items: [], agents: {}, status: 'running', startedAt: Date.now() }],
      };
    case 'stream':
      return a.runId === state.runId ? applyEvent(state, a.event) : state;
    case 'runEnded': {
      if (a.runId !== state.runId) return state;
      const s = updateCurrent(state, (t) => (t.status === 'running' ? { ...t, status: a.exitCode === 0 ? 'done' : 'error' } : t));
      return { ...s, runId: null, approvals: [] };
    }
    case 'sessions': return { ...state, sessions: a.sessions };
    case 'sessionLoaded':
      return { ...initialState, ...keep(state), sessionId: a.id, title: a.title, turns: fromTranscript(a.messages) };
    case 'sessionRenamed':
      return a.id === state.sessionId ? { ...state, title: a.title } : state;
    case 'newChat':
      return { ...initialState, ...keep(state) };
    case 'settings': return { ...state, settings: a.settings };
    case 'providers': return { ...state, providers: a.providers };
    case 'addChip':
    case 'local/addChip':
      return { ...state, view: 'chat', chips: [...state.chips.filter((c) => !(c.path === a.chip.path && c.startLine === a.chip.startLine && c.kind === a.chip.kind)), a.chip] };
    case 'prefill': return { ...state, view: 'chat', draft: { text: a.text, send: !!a.send, nonce: Date.now() } };
    case 'fileResults': return { ...state, files: { query: a.query, items: a.items } };
    case 'memoryResults': return { ...state, memory: { query: a.query, results: a.results, added: a.added } };
    case 'tools': return { ...state, tools: a.tools };
    case 'hostError': return { ...state, hostError: { message: a.message, fix: a.fix, provider: a.provider } };
    case 'notice': return updateCurrent(state, (t) => ({ ...t, items: [...t.items, { kind: 'notice', key: key(), text: a.text }] }));
    case 'open':
      if (a.view === 'sessions' || a.view === 'model') return { ...state, view: 'chat', openRequest: { view: a.view, nonce: Date.now() } };
      return { ...state, view: a.view };
    case 'focusInput': return { ...state, view: 'chat', draft: { text: '', send: false, nonce: Date.now() } };
    case 'local/setMode': return { ...state, mode: a.mode };
    case 'local/setModel': return { ...state, model: a.model };
    case 'local/view': return { ...state, view: a.view };
    case 'local/removeChip': return { ...state, chips: state.chips.filter((_, i) => i !== a.index) };
    case 'local/clearChips': return { ...state, chips: [] };
    case 'local/answered': {
      const req = state.approvals.find((x) => x.id === a.id);
      const s = { ...state, approvals: state.approvals.filter((x) => x.id !== a.id) };
      if (!req || a.decision === 'deny') return s;
      return updateCurrent(s, (t) => updateTool(t, req.tool_call_id, (i) => ({ ...i, status: 'running' })));
    }
    case 'local/dismissHostError': return { ...state, hostError: null };
    case 'selection': return { ...state, selection: a.chip };
    case 'local/dismissSelection': return { ...state, selectionDismissed: state.selection ? selectionKey(state.selection) : null };
    default: return state;
  }
}

/** What survives "New chat" and opening another session. */
function keep(s: State): Partial<State> {
  return {
    ready: s.ready, mode: s.mode, model: s.model, providers: s.providers, sessions: s.sessions, settings: s.settings,
    inTab: s.inTab, workspaceName: s.workspaceName, tools: s.tools, chips: s.chips, selection: s.selection, selectionDismissed: s.selectionDismissed,
  };
}

export const selectionKey = (c: Chip) => `${c.path}:${c.startLine}-${c.endLine}:${(c.text ?? '').length}`;

/** The automatic selection chip, unless the user removed it or already attached the same range. */
export function autoChip(s: State): Chip | null {
  const c = s.selection;
  if (!c || s.selectionDismissed === selectionKey(c)) return null;
  if (s.chips.some((x) => x.path === c.path && x.startLine === c.startLine && x.endLine === c.endLine)) return null;
  return c;
}

// ── Derived values ────────────────────────────────────────────────────────────────────

export function isRunning(s: State): boolean { return s.runId !== null; }

/** What the status line shows: the newest running step, else "Thinking". */
export function activity(t: Turn | undefined): string {
  if (!t) return 'Thinking';
  const running = [...t.items].reverse().find((i) => i.kind === 'tool' && (i.status === 'running' || i.status === 'waiting'));
  if (running && running.kind === 'tool') return running.status === 'waiting' ? 'Waiting for approval' : running.summary;
  const last = t.items[t.items.length - 1];
  return last?.kind === 'text' ? 'Writing' : 'Thinking';
}

export function formatTokens(n: number): string {
  if (n < 1000) return `${n}`;
  if (n < 100_000) return `${(n / 1000).toFixed(1).replace(/\.0$/, '')}k`;
  return `${Math.round(n / 1000)}k`;
}

export function formatCost(usd: number): string {
  if (!usd) return '$0';
  return usd < 0.01 ? `$${usd.toFixed(4)}` : `$${usd.toFixed(2)}`;
}

export function formatDuration(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return `${m}m ${s % 60}s`;
}

/** Group sessions for the header menu: Today, Yesterday, Earlier. */
export function groupSessions(rows: SessionRow[], query: string, now = new Date()): { label: string; rows: SessionRow[] }[] {
  const q = query.trim().toLowerCase();
  const filtered = q ? rows.filter((r) => r.title.toLowerCase().includes(q)) : rows;
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const day = 86_400_000;
  const groups: Record<string, SessionRow[]> = { Today: [], Yesterday: [], Earlier: [] };
  for (const r of filtered) {
    const t = Date.parse(r.updated_at || r.created_at) || 0;
    groups[t >= start ? 'Today' : t >= start - day ? 'Yesterday' : 'Earlier'].push(r);
  }
  return Object.entries(groups).filter(([, v]) => v.length).map(([label, rows]) => ({ label, rows }));
}

/** File references such as `src/app.ts:12` or `sample.js` in replies. */
export const FILE_REF = /^(?:\.{0,2}\/)?[\w@.\-/\\]+\.[A-Za-z0-9]{1,8}(?::(\d+)(?::\d+)?(?:-\d+)?)?$/;
export function parseFileRef(text: string): { path: string; line?: number } | null {
  const t = text.trim();
  if (!FILE_REF.test(t) || /^https?:/i.test(t) || /^\d+(\.\d+)+$/.test(t)) return null;
  const m = /^(.*?)(?::(\d+))?(?::\d+)?(?:-\d+)?$/.exec(t);
  if (!m) return null;
  return { path: m[1], line: m[2] ? Number(m[2]) : undefined };
}
