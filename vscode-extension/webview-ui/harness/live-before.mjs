// Captures the previous (v2) chat for before/after comparisons. Same setup as live.mjs.
// Live UI test: launches an isolated VS Code with this extension (development mode), connects over
// CDP (Playwright's _electron.launch fails on current VS Code builds) and drives the Solvra panel
// against the real backend in a THROWAWAY workspace with a low budget.
//
//   VSCODE=/path/to/code SOLVRA_ROOT=/path/to/repo node harness/live.mjs <outDir> [theme]
//
// Uses a short --user-data-dir (long paths break VS Code's IPC socket) and strips ELECTRON_* /
// VSCODE_* variables so it also works from a terminal inside VS Code.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const ext = path.resolve(here, '../..');
const out = path.resolve(process.argv[2] || '/tmp/sv-live');
const theme = process.argv[3] || 'Default Dark Modern';
const tag = 'before-' + (theme.toLowerCase().includes('light') ? 'light' : 'dark');
const code = process.env.VSCODE || '/tmp/svcode/code';
const root = process.env.SOLVRA_ROOT || path.resolve(ext, '..');
const model = process.env.SOLVRA_MODEL || 'chatgpt:gpt-5.6-luna';
const prof = '/tmp/svprof', work = '/tmp/svdemo', sessions = '/tmp/svsessions-live';
fs.mkdirSync(out, { recursive: true });
for (const d of [prof, work]) fs.rmSync(d, { recursive: true, force: true });
fs.mkdirSync(path.join(prof, 'User'), { recursive: true });
fs.mkdirSync(work, { recursive: true });
fs.mkdirSync(sessions, { recursive: true });
fs.writeFileSync(path.join(work, 'sample.js'), 'const items = [];\nconst total = items.reduce((a, b) => a + b);\nconsole.log(total);\n');
fs.writeFileSync(path.join(work, 'notes.txt'), 'line one\nline two\n');
fs.writeFileSync(path.join(prof, 'User', 'settings.json'), JSON.stringify({
  'workbench.colorTheme': theme, 'workbench.startupEditor': 'none', 'security.workspace.trust.enabled': false,
  'telemetry.telemetryLevel': 'off', 'workbench.welcomePage.walkthroughs.openOnInstall': false, 'chat.disableAIFeatures': true, 'update.mode': 'none', 'extensions.autoUpdate': false, 'window.titleBarStyle': 'custom',
  'solvra.solvraPath': root, 'solvra.model': model, 'solvra.maxBudget': 0.25, 'solvra.effort': 'low',
  // A plain-text key left by an older version: must be migrated to secret storage and removed.
  'solvra.ui.version': 'v2', 'solvra.autoApprove': true,
}, null, 2));

const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(ELECTRON_|VSCODE_)/.test(k)));
env.SOLVRA_SESSIONS_DIR = sessions;
const port = 9300 + Math.floor(Math.random() * 500);
const proc = spawn(code, ['--user-data-dir', prof, '--extensions-dir', '/tmp/svext', '--extensionDevelopmentPath', ext,
  '--disable-extensions', '--remote-debugging-port=' + port, '--no-sandbox', '--disable-gpu-sandbox', '--password-store=basic', '--new-window', work], { env, stdio: ['ignore', fs.openSync(path.join(out, 'vscode.log'), 'w'), fs.openSync(path.join(out, 'vscode.err.log'), 'w')] });
process.on('exit', () => { try { proc.kill('SIGKILL'); } catch { /* gone */ } });

const log = [];
const step = (s) => { log.push(s); console.log(s); };
async function connect() {
  for (let i = 0; i < 60; i++) {
    try { return await chromium.connectOverCDP(`http://127.0.0.1:${port}`); } catch { await new Promise((r) => setTimeout(r, 1000)); }
  }
  throw new Error('VS Code did not open a debugging port');
}
const browser = await connect();
let page;
for (let i = 0; i < 60 && !page; i++) {
  page = browser.contexts().flatMap((c) => c.pages()).find((p) => p.url().includes('workbench'));
  if (!page) await new Promise((r) => setTimeout(r, 500));
}
await page.setViewportSize?.({ width: 1400, height: 900 }).catch(() => {});
await page.waitForSelector('.monaco-workbench', { timeout: 60000 });
step('workbench ready');
// A fresh profile shows a first-run welcome overlay; close it.
for (let i = 0; i < 10; i++) {
  if (!(await page.locator('.onboarding-a-overlay.visible').count())) break;
  await page.keyboard.press('Escape');
  await page.waitForTimeout(500);
  if (i > 3) await page.evaluate(() => document.querySelectorAll('.onboarding-a-overlay').forEach((e) => e.remove()));
}

async function chatFrame() {
  for (let i = 0; i < 60; i++) {
    for (const f of page.frames()) {
      try { if (await f.$('#input')) return f; } catch { /* detached */ }
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  await page.screenshot({ path: path.join(out, `${tag}-failure.png`) });
  for (const fr of page.frames()) {
    const html = await fr.evaluate(() => document.body?.outerHTML.slice(0, 600)).catch((e) => `(${e.message})`);
    console.log('FRAME', fr.url().slice(0, 100), '\n   ', html);
  }
  throw new Error('Solvra webview not found');
}
const shot = async (name) => { await page.screenshot({ path: path.join(out, `${tag}-${name}.png`) }); step(`shot ${name}`); };

await page.waitForSelector('.activitybar [aria-label^="Solvra"]', { timeout: 30000 });
await page.waitForTimeout(2000);
for (let i = 0; i < 4; i++) {
  if (await page.locator('.part.sidebar .composite.title', { hasText: /solvra/i }).isVisible().catch(() => false)) break;
  await page.click('.activitybar [aria-label^="Solvra"]');
  await page.waitForTimeout(2500);
}
const f = await chatFrame();
await page.waitForTimeout(1500);
await shot('01-empty');
await f.fill('#input', 'Fix the bug in sample.js so an empty list prints 0. Use file_edit, then run node sample.js.');
await f.press('#input', 'Enter');
await page.waitForTimeout(6000);
await shot('02-running');
await page.waitForTimeout(40000);
await shot('03-done');
await browser.close().catch(() => {});
proc.kill('SIGTERM');
