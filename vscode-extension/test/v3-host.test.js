// Unit tests for the v3 host modules (compiled to out/ by `npm test`).
const test = require('node:test');
const assert = require('node:assert/strict');
const { NdjsonParser } = require('../out/ndjson');
const { KeyStore, migrateSettingsKeys, secretName } = require('../out/keys');
const { buildProviderGroups, qualifyModel, splitModel, parseModelsJson } = require('../out/models');
const { buildPrompt, titleFrom } = require('../out/prompt');
const { buildStreamArgs } = require('../out/stream-run');

test('ndjson: splits chunks across lines, ignores blanks, reports non-JSON', () => {
  const got = []; const other = [];
  const p = new NdjsonParser((v) => got.push(v), (l) => other.push(l));
  p.push('{"type":"session","id":"a"}\n{"type":"text_');
  p.push('delta","agent":"main","text":"hi"}\r\n\n');
  p.push('warning: not json\n{"no_type":1}\n{"type":"done"}');
  assert.equal(got.length, 2);
  p.end();
  assert.deepEqual(got.map((g) => g.type), ['session', 'text_delta', 'done']);
  assert.equal(got[1].text, 'hi');
  assert.deepEqual(other, ['warning: not json', '{"no_type":1}']);
});

function fakeSecrets() {
  const m = new Map();
  return { m, get: async (k) => m.get(k), store: async (k, v) => { m.set(k, v); }, delete: async (k) => { m.delete(k); } };
}

test('keys: plain-text settings move to secret storage and are removed from every scope', async () => {
  const secrets = fakeSecrets();
  const values = {
    googleApiKey: { globalValue: 'g-global', workspaceValue: 'g-work' },
    moonshotApiKey: { globalValue: '  m-key  ' },
    anthropicApiKey: { globalValue: '' },
    openaiApiKey: {},
  };
  const updates = [];
  const config = {
    inspect: (k) => values[k],
    update: async (k, v, target) => { updates.push([k, v, target]); },
  };
  secrets.m.set(secretName('openai'), 'already');
  const report = await migrateSettingsKeys(config, secrets);
  assert.deepEqual(report.migrated.sort(), ['google', 'moonshot']);
  assert.equal(secrets.m.get(secretName('google')), 'g-work'); // workspace wins over global
  assert.equal(secrets.m.get(secretName('moonshot')), 'm-key');
  assert.equal(secrets.m.get(secretName('openai')), 'already');
  assert.ok(!secrets.m.has(secretName('anthropic')));
  // Every scope that had a value (even an empty one) is cleared.
  assert.deepEqual(updates.filter(([, v]) => v === undefined).map(([k, , t]) => `${k}@${t}`).sort(),
    ['anthropicApiKey@1', 'googleApiKey@1', 'googleApiKey@2', 'moonshotApiKey@1']);
});

test('keys: environment for the backend uses stored keys over inherited ones', async () => {
  const secrets = fakeSecrets();
  const store = new KeyStore(secrets, { PATH: '/bin', GOOGLE_API_KEY: 'from-env', ANTHROPIC_API_KEY: 'env-a' });
  await store.set('google', ' stored ');
  const env = await store.processEnv();
  assert.equal(env.GOOGLE_API_KEY, 'stored');
  assert.equal(env.GEMINI_API_KEY, 'stored');
  assert.equal(env.ANTHROPIC_API_KEY, 'env-a');
  assert.equal(env.PATH, '/bin');
  assert.equal(await store.has('anthropic'), true);
  assert.equal(await store.has('moonshot'), false);
  await assert.rejects(() => store.set('google', '   '));
});

test('models: provider:model is always explicit, groups report readiness', () => {
  assert.equal(qualifyModel('claude-sonnet-5'), 'anthropic:claude-sonnet-5');
  assert.equal(qualifyModel('chatgpt:gpt-5.6-sol'), 'chatgpt:gpt-5.6-sol');
  assert.equal(qualifyModel('my-model', 'ollama'), 'ollama:my-model');
  assert.deepEqual(splitModel('openai:gpt-4.1'), { provider: 'openai', model: 'gpt-4.1' });
  const groups = buildProviderGroups((p) => p === 'google', { google: ['google:gemini-3-pro', 'gemini-2.5-flash'] }, false);
  const by = Object.fromEntries(groups.map((g) => [g.provider, g]));
  assert.equal(by.google.ready, true);
  assert.equal(by.anthropic.ready, false);
  assert.equal(by.chatgpt.ready, false); // not signed in
  assert.equal(by.ollama.ready, true);
  assert.deepEqual(by.google.models.map((m) => m.id), ['google:gemini-3-pro', 'google:gemini-2.5-flash']);
  // One entry per provider: no duplicate "openai" / "chatgpt" prompt before the model.
  assert.equal(new Set(groups.map((g) => g.provider)).size, groups.length);
  assert.deepEqual(parseModelsJson('{"provider":"google","models":[{"id":"google:a"},{"id":"google:b"}]}'), ['google:a', 'google:b']);
  assert.deepEqual(parseModelsJson('nope'), []);
});

test('prompt: chips become references, titles come from the user words', () => {
  const chips = [
    { kind: 'file', path: 'src/a.ts', label: 'a.ts' },
    { kind: 'folder', path: 'data', label: 'data/' },
    { kind: 'selection', path: 'src/b.ts', label: 'b.ts:3-4', startLine: 3, endLine: 4, text: 'let x = 1;\nlet y = 2;' },
  ];
  const p = buildPrompt('  Why is y unused? ', chips);
  assert.match(p, /Files the user attached \(read them as needed\): src\/a\.ts/);
  assert.match(p, /Folders the user attached .*: data/);
  assert.match(p, /<selection path="src\/b\.ts" lines="3-4">\nlet x = 1;\nlet y = 2;\n<\/selection>/);
  assert.ok(p.endsWith('Why is y unused?'));
  assert.equal(titleFrom('\n  hi   there \nsecond', chips), 'hi there');
  assert.equal(titleFrom('', chips), 'About src/a.ts');
});

test('stream args: modes map to permissions; plan never pairs with --auto; session vs title', () => {
  const base = { prefix: ['S.dll'], prompt: 'p', model: 'chatgpt:gpt-5.6-sol' };
  const ask = buildStreamArgs({ ...base, mode: 'ask', title: 'T' });
  assert.ok(!ask.includes('--auto') && !ask.includes('--plan'));
  assert.equal(ask[ask.indexOf('--permission-mode') + 1], 'default');
  assert.equal(ask[ask.indexOf('--output-format') + 1], 'stream-json');
  assert.equal(ask[ask.indexOf('--title') + 1], 'T');
  const plan = buildStreamArgs({ ...base, mode: 'plan' });
  assert.ok(plan.includes('--plan') && !plan.includes('--auto'));
  const auto = buildStreamArgs({ ...base, mode: 'auto', sessionId: 's1', title: 'ignored', allowRules: ['edit', 'execute:node'], maxBudget: 0.5, maxTurns: 0, effort: 'high' });
  assert.ok(auto.includes('--auto'));
  assert.equal(auto[auto.indexOf('--session') + 1], 's1');
  assert.ok(!auto.includes('--title'));
  assert.deepEqual(auto.filter((a, i) => auto[i - 1] === '--allow-rule'), ['edit', 'execute:node']);
  assert.equal(auto[auto.indexOf('--max-budget') + 1], '0.5');
  assert.equal(auto[auto.indexOf('--effort') + 1], 'high');
});
