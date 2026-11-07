// Host messages replayed into the webview for each screenshot. Same shapes the extension sends.
const settings = {
  values: { model: 'chatgpt:gpt-5.6-sol', effort: 'medium', maxTurns: 0, maxBudget: 0.5, autoApprove: false, executablePath: '', dotnetPath: '' },
  keys: [
    { provider: 'anthropic', label: 'Anthropic', set: true, fromEnv: false, url: 'https://console.anthropic.com/settings/keys' },
    { provider: 'openai', label: 'OpenAI', set: false, fromEnv: true, url: 'https://platform.openai.com/api-keys' },
    { provider: 'google', label: 'Google Gemini', set: false, fromEnv: false, url: 'https://aistudio.google.com/apikey' },
    { provider: 'moonshot', label: 'Moonshot (Kimi)', set: false, fromEnv: false, url: 'https://platform.moonshot.ai' },
    { provider: 'brave', label: 'Brave Search (web search)', set: false, fromEnv: false, url: 'https://brave.com/search/api/' },
  ],
};
const g = (provider, label, auth, ready, models) => ({ provider, label, auth, ready, models: models.map((m) => ({ id: `${provider}:${m}`, label: m, provider })) });
const providers = [
  g('chatgpt', 'ChatGPT (Codex sign-in)', 'codex', true, ['gpt-5.6-sol', 'gpt-5.6-luna', 'gpt-6.1-sol']),
  g('anthropic', 'Anthropic', 'key', true, ['claude-opus-5', 'claude-sonnet-5', 'claude-haiku-4-5']),
  g('google', 'Google Gemini', 'key', false, ['gemini-2.5-pro', 'gemini-2.5-flash']),
  g('ollama', 'Ollama (local)', 'none', true, ['llama3.1']),
];
const h = (n) => new Date(Date.now() - n * 3600_000).toISOString();
const sessions = [
  { id: 's1', title: 'Bump a in sample.js', created_at: h(1), updated_at: h(0.5), model: 'gpt-5.6-sol', provider: 'chatgpt' },
  { id: 's2', title: 'Explain the stream-json protocol', created_at: h(3), updated_at: h(2), model: 'gpt-5.6-sol', provider: 'chatgpt' },
  { id: 's3', title: 'Fix flaky session tests', created_at: h(30), updated_at: h(28), model: 'claude-sonnet-5', provider: 'anthropic' },
  { id: 's4', title: 'Add Arabic font fallback', created_at: h(200), updated_at: h(190), model: 'gpt-5.6-luna', provider: 'chatgpt' },
];
const init = (extra = {}) => ({ type: 'init', state: { mode: 'ask', model: 'chatgpt:gpt-5.6-sol', settings, providers, sessions, workspaceName: 'svdemo', inTab: false, ...extra } });
const ev = (event) => ({ type: 'stream', runId: 'r1', event });
const diff = '--- a/sample.js\n+++ b/sample.js\n@@ -1,4 +1,4 @@\n const items = load();\n-const total = items.reduce((a, b) => a + b);\n+const total = items.reduce((a, b) => a + b, 0);\n console.log(total);\n module.exports = total;\n';

const start = (text, chips = []) => [{ type: 'runStarted', runId: 'r1', text, chips }, ev({ type: 'session', id: 'abc', title: text.split('\n')[0].slice(0, 60), model: 'gpt-5.6-sol', provider: 'chatgpt', cwd: '/tmp/svdemo', mode: 'ask', effort: 'medium', resumed: false, rules: [] })];
const readStep = (id = 't1') => [
  ev({ type: 'tool_call', agent: 'main', id, name: 'file_read', input: { path: 'sample.js' }, summary: 'Read sample.js', kind: 'read' }),
  ev({ type: 'tool_result', agent: 'main', id, ok: true, output: '1\tconst items = load();\n2\tconst total = items.reduce((a, b) => a + b);\n3\tconsole.log(total);\n4\tmodule.exports = total;', diff: null, meta: { added: null, removed: null, lines: 4 } }),
];
const editCall = ev({ type: 'tool_call', agent: 'main', id: 't2', name: 'file_edit', input: { path: 'sample.js', old_string: 'a + b)', new_string: 'a + b, 0)' }, summary: 'Edit sample.js', kind: 'edit' });
const plan = (s) => ev({ type: 'plan', agent: 'main', items: [{ text: 'Read sample.js', status: 'done' }, { text: 'Give reduce an initial value', status: s }, { text: 'Run node sample.js', status: 'pending' }] });
const usage = (i, o, pct) => ev({ type: 'usage', agent: 'main', input: i, output: o, cost_usd: 0.0042, context_pct: pct });

export const SCENARIOS = {
  empty: [init()],
  streaming: [init(), ...start('Why does sample.js print NaN for an empty list? Fix it.', [{ kind: 'selection', path: 'sample.js', label: 'sample.js:2', startLine: 2, endLine: 2, text: 'const total = items.reduce((a, b) => a + b);' }]),
    plan('active'), ...readStep(), usage(5200, 140, 12),
    ev({ type: 'text_delta', agent: 'main', text: '`reduce` without an initial value throws on an empty array, and here `items` can be empty, so the sum is never a number. The fix is on `sample.js:2`:\n\n```js\nconst total = items.reduce((a, b) => a + b, 0);\n```\n\nI’ll make that edit and then run' })],
  approval: [init(), ...start('Why does sample.js print NaN for an empty list? Fix it.'), plan('active'), ...readStep(), usage(6100, 210, 14),
    ev({ type: 'text_delta', agent: 'main', text: 'The `reduce` call needs an initial value of `0`.' }), editCall,
    ev({ type: 'permission_request', id: 'p1', agent: 'main', tool_call_id: 't2', tool: 'file_edit', kind: 'edit', summary: 'Edit sample.js', diff, command: null, always: 'edit' })],
  diff: [init(), ...start('Why does sample.js print NaN for an empty list? Fix it.'), plan('done'), ...readStep(),
    ev({ type: 'text_delta', agent: 'main', text: 'The `reduce` call needs an initial value of `0`.' }), editCall,
    ev({ type: 'tool_result', agent: 'main', id: 't2', ok: true, output: 'Edited sample.js at line 2.', diff, meta: { added: 1, removed: 1, lines: 1 } }),
    ev({ type: 'tool_call', agent: 'main', id: 't3', name: 'bash', input: { command: 'node sample.js' }, summary: 'Run node sample.js', kind: 'execute' }),
    ev({ type: 'tool_result', agent: 'main', id: 't3', ok: true, output: '0\n', diff: null, meta: { added: null, removed: null, lines: 1 } }),
    ev({ type: 'text_delta', agent: 'main', text: 'Fixed. `sample.js:2` now passes `0` as the initial value, and `node sample.js` prints `0` for an empty list.' }),
    ev({ type: 'done', stop_reason: 'text', text: '', error: null, session_id: 'abc', turns: 4, duration_ms: 14000, cost_usd: 0.0042, usage: { input: 10377, output: 236 } }),
    { type: 'runEnded', runId: 'r1', exitCode: 0 }],
  subagents: [init(), ...start('Count the lines in sample.js and notes.txt using two agents in parallel.'),
    ev({ type: 'tool_call', agent: 'main', id: 'ag1', name: 'agent', input: { prompt: 'Count lines of sample.js', description: 'Count sample.js lines' }, summary: 'Agent: Count sample.js lines', kind: 'agent' }),
    ev({ type: 'tool_call', agent: 'main', id: 'ag2', name: 'agent', input: { prompt: 'Count lines of notes.txt', description: 'Count notes.txt lines' }, summary: 'Agent: Count notes.txt lines', kind: 'agent' }),
    ev({ type: 'agent_start', agent: 'a1', parent: 'main', tool_call_id: 'ag1', title: 'Count sample.js lines' }),
    ev({ type: 'agent_start', agent: 'a2', parent: 'main', tool_call_id: 'ag2', title: 'Count notes.txt lines' }),
    ev({ type: 'tool_call', agent: 'a1', id: 'x1', name: 'bash', input: { command: 'wc -l sample.js' }, summary: 'Run wc -l sample.js', kind: 'execute' }),
    ev({ type: 'tool_call', agent: 'a2', id: 'x2', name: 'bash', input: { command: 'wc -l notes.txt' }, summary: 'Run wc -l notes.txt', kind: 'execute' }),
    ev({ type: 'tool_result', agent: 'a2', id: 'x2', ok: true, output: '2 notes.txt', diff: null, meta: { added: null, removed: null, lines: 1 } }),
    ev({ type: 'text_delta', agent: 'a2', text: 'notes.txt has 2 lines.' }),
    ev({ type: 'agent_end', agent: 'a2', status: 'ok' }), usage(15454, 295, 9)],
  multidelta: [init(), ...start('Fix it'), ...'Fixed `sample.js` and checked `src/app.ts:12`; ran `node sample.js`.'.match(/.{1,3}/g).map((t) => ev({ type: 'text_delta', agent: 'main', text: t })),
    ev({ type: 'done', stop_reason: 'text', text: '', error: null, session_id: 'abc', turns: 1, duration_ms: 1000, cost_usd: 0, usage: { input: 1, output: 1 } }), { type: 'runEnded', runId: 'r1', exitCode: 0 }],
  error: [init(), ...start('Summarise the README'), ev({ type: 'error', message: 'No Google Gemini API key is set.', fix: 'add_api_key', provider: 'google' }), { type: 'runEnded', runId: 'r1', exitCode: null }],
  arabic: [init(), ...start('لماذا يطبع الملف sample.js القيمة NaN عندما تكون القائمة فارغة؟'), ...readStep(),
    ev({ type: 'text_delta', agent: 'main', text: 'الدالة `reduce` بدون قيمة ابتدائية لا تعمل مع مصفوفة فارغة. الحل في `sample.js:2` هو إضافة القيمة `0`:\n\n```js\nconst total = items.reduce((a, b) => a + b, 0);\n```\n\nبعد التعديل يطبع البرنامج `0`.' }),
    ev({ type: 'done', stop_reason: 'text', text: '', error: null, session_id: 'abc', turns: 2, duration_ms: 6000, cost_usd: 0, usage: { input: 4200, output: 120 } }),
    { type: 'runEnded', runId: 'r1', exitCode: 0 }],
  settings: [init(), { type: 'open', view: 'settings' }],
  memory: [init(), { type: 'open', view: 'memory' }, { type: 'memoryResults', query: 'tests', results: ['Tests live in tests/ and run with npm test.', 'Use xUnit for .NET tests.'] }],
  tools: [init(), { type: 'open', view: 'tools' }, { type: 'tools', tools: [
    { name: 'file_read', description: 'Read a file with line numbers.', kind: 'read' }, { name: 'grep', description: 'Search file contents.', kind: 'read' },
    { name: 'web_fetch', description: 'Fetch a URL.', kind: 'network' }, { name: 'file_edit', description: 'Replace exact text in a file.', kind: 'edit' },
    { name: 'bash', description: 'Run a shell command.', kind: 'execute' }, { name: 'agent', description: 'Delegate a sub-task to a subagent.', kind: 'agent' }] }],
  models: [init(), { type: 'open', view: 'model' }],
  sessionsMenu: [init(), ...start('Bump a in sample.js'), { type: 'open', view: 'sessions' }],
  slash: [init()],
  mention: [init(), { type: 'fileResults', query: 'sa', items: [{ path: 'src', label: 'src/', kind: 'folder' }, { path: 'sample.js', label: 'sample.js', kind: 'file' }, { path: 'src/sample.test.js', label: 'sample.test.js', kind: 'file' }] }],
};
