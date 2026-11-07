// Screenshots of every v3 state in Chromium with a mock host. Usage: node harness/shoot.mjs <outDir>
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const ext = path.resolve(here, '../..');
const out = path.resolve(process.argv[2] || path.join(here, 'out'));
fs.mkdirSync(out, { recursive: true });
const types = { '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.woff2': 'font/woff2', '.woff': 'font/woff', '.ttf': 'font/ttf', '.svg': 'image/svg+xml' };
const server = http.createServer((req, res) => {
  const url = decodeURIComponent(req.url.split('?')[0]);
  const file = url.startsWith('/media/') ? path.join(ext, url) : path.join(here, url === '/' ? 'index.html' : url);
  if (url === '/favicon.ico') { res.writeHead(204); res.end(); return; }
  if (!file.startsWith(ext) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen(0);
const port = server.address().port;
const exe = process.env.CHROME || ['chromium-1254', 'chromium-1248', 'chromium-1243'].map((d) => path.join(process.env.HOME, '.cache/ms-playwright', d, 'chrome-linux64/chrome')).find((p) => fs.existsSync(p));
const browser = await chromium.launch({ executablePath: exe });

const shots = [
  ['01-empty-light', 'empty', 'light'], ['02-empty-dark', 'empty', 'dark'],
  ['03-streaming-light', 'streaming', 'light'], ['04-streaming-dark', 'streaming', 'dark'],
  ['05-approval-light', 'approval', 'light'], ['06-approval-dark', 'approval', 'dark'],
  ['07-diff-expanded-light', 'diff', 'light', async (p) => { await p.getByRole('button', { name: /Edit\s*sample\.js/ }).click(); }],
  ['08-diff-expanded-dark', 'diff', 'dark', async (p) => { await p.getByRole('button', { name: /Edit\s*sample\.js/ }).click(); }],
  ['09-subagents-light', 'subagents', 'light'], ['10-subagents-dark', 'subagents', 'dark'],
  ['11-settings-light', 'settings', 'light'], ['12-settings-dark', 'settings', 'dark'],
  ['13-arabic-light', 'arabic', 'light'], ['14-arabic-dark', 'arabic', 'dark'],
  ['15-error-fix-light', 'error', 'light'],
  ['16-model-picker-dark', 'models', 'dark'], ['17-session-menu-light', 'sessionsMenu', 'light'],
  ['18-slash-commands-dark', 'slash', 'dark', async (p) => { await p.getByLabel('Message Solvra').fill(''); await p.getByLabel('Message Solvra').type('/'); }],
  ['19-mention-light', 'mention', 'light', async (p) => { await p.getByLabel('Message Solvra').type('Look at @sa'); }],
  ['20-memory-dark', 'memory', 'dark'], ['21-tools-light', 'tools', 'light'],
  ['22-approval-hc', 'approval', 'hc'], ['23-diff-hc', 'diff', 'hc', async (p) => { await p.getByRole('button', { name: /Edit\s*sample\.js/ }).click(); }],
  ['24-tab-wide-dark', 'diff', 'dark', null, { width: 900, height: 760, surface: 'tab' }],
];
const errors = [];
for (const [name, s, theme, act, opt] of shots) {
  const page = await browser.newPage({ viewport: { width: opt?.width ?? 400, height: opt?.height ?? 760 }, deviceScaleFactor: Number(process.env.SCALE || 2) });
  page.on('pageerror', (e) => errors.push(`${name}: ${e.message}`));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`${name}: ${m.text()}`); });
  await page.goto(`http://127.0.0.1:${port}/?s=${s}&theme=${theme}&surface=${opt?.surface ?? 'sidebar'}`);
  await page.waitForSelector('.app');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);
  if (act) { await act(page); await page.waitForTimeout(350); }
  await page.screenshot({ path: path.join(out, `${name}.png`) });
  await page.close();
}
// Keyboard: approval answered with "2" sends allow_always for p1.
const page = await browser.newPage({ viewport: { width: 400, height: 760 } });
await page.goto(`http://127.0.0.1:${port}/?s=approval&theme=light`);
await page.waitForSelector('.approval');
await page.keyboard.press('2');
const sent = await page.evaluate(() => window.__sent.filter((m) => m.type === 'permission'));
// ⇧Tab cycles the mode.
await page.goto(`http://127.0.0.1:${port}/?s=empty&theme=light`);
await page.waitForSelector('.composer');
await page.keyboard.press('Shift+Tab');
const modes = await page.evaluate(() => window.__sent.filter((m) => m.type === 'setMode').map((m) => m.mode));
await browser.close();
server.close();
console.log(JSON.stringify({ shots: shots.length, out, keyboardApproval: sent, shiftTabModes: modes, errors }, null, 2));
if (errors.length || sent[0]?.decision !== 'allow_always' || modes[0] !== 'plan') process.exit(1);
