// Shared launcher for the live harnesses: an isolated VS Code with this extension in development
// mode, reached over CDP. Short --user-data-dir (long paths break VS Code's IPC socket),
// ELECTRON_*/VSCODE_* removed, basic password store (no OS keyring in CI/Xvfb).
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
export const ext = path.resolve(here, '../..');

export async function launch({ out, tag, open, settings = {}, sessions = '/tmp/svsessions-live', prof = '/tmp/svprof' }) {
  fs.mkdirSync(out, { recursive: true });
  fs.rmSync(prof, { recursive: true, force: true });
  fs.mkdirSync(path.join(prof, 'User'), { recursive: true });
  fs.mkdirSync(sessions, { recursive: true });
  fs.writeFileSync(path.join(prof, 'User', 'settings.json'), JSON.stringify({
    'workbench.startupEditor': 'none', 'security.workspace.trust.enabled': false, 'telemetry.telemetryLevel': 'off',
    'update.mode': 'none', 'workbench.welcomePage.walkthroughs.openOnInstall': false, 'chat.disableAIFeatures': true,
    'solvra.solvraPath': process.env.SOLVRA_ROOT || path.resolve(ext, '..'), 'solvra.model': process.env.SOLVRA_MODEL || 'chatgpt:gpt-5.6-luna',
    'solvra.maxBudget': 0.25, 'solvra.effort': 'low', ...settings,
  }, null, 2));
  const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(ELECTRON_|VSCODE_)/.test(k)));
  env.SOLVRA_SESSIONS_DIR = sessions;
  const port = 9300 + Math.floor(Math.random() * 500);
  const proc = spawn(process.env.VSCODE || '/tmp/svcode/code', ['--user-data-dir', prof, '--extensions-dir', '/tmp/svext',
    '--extensionDevelopmentPath', ext, '--disable-extensions', '--remote-debugging-port=' + port, '--no-sandbox',
    '--disable-gpu-sandbox', '--password-store=basic', '--new-window', open],
    { env, stdio: ['ignore', fs.openSync(path.join(out, `${tag}-vscode.log`), 'w'), fs.openSync(path.join(out, `${tag}-vscode.err.log`), 'w')] });
  process.on('exit', () => { try { proc.kill('SIGKILL'); } catch { /* gone */ } });

  let browser;
  for (let i = 0; i < 60 && !browser; i++) {
    try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`); } catch { await new Promise((r) => setTimeout(r, 1000)); }
  }
  if (!browser) throw new Error('VS Code did not open a debugging port');
  let page;
  for (let i = 0; i < 60 && !page; i++) {
    page = browser.contexts().flatMap((c) => c.pages()).find((p) => p.url().includes('workbench'));
    if (!page) await new Promise((r) => setTimeout(r, 500));
  }
  await page.waitForSelector('.monaco-workbench', { timeout: 60000 });
  for (let i = 0; i < 10; i++) {
    if (!(await page.locator('.onboarding-a-overlay.visible').count())) break;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(500);
    if (i > 3) await page.evaluate(() => document.querySelectorAll('.onboarding-a-overlay').forEach((e) => e.remove()));
  }
  const log = [];
  const step = (s) => { log.push(s); console.log(s); };
  const shot = async (name) => { await page.screenshot({ path: path.join(out, `${tag}-${name}.png`) }); step(`shot ${name}`); };
  async function openSolvra() {
    await page.waitForSelector('.activitybar [aria-label^="Solvra"]', { timeout: 30000 });
    await page.waitForTimeout(2000);
    for (let i = 0; i < 4; i++) {
      if (await page.locator('.part.sidebar .composite.title', { hasText: /solvra/i }).isVisible().catch(() => false)) break;
      await page.click('.activitybar [aria-label^="Solvra"]');
      await page.waitForTimeout(2500);
    }
  }
  async function chatFrame(selector = '.app .composer, .app .approval, .app .view') {
    for (let i = 0; i < 60; i++) {
      for (const f of page.frames()) { try { if (await f.$(selector)) return f; } catch { /* detached */ } }
      await new Promise((r) => setTimeout(r, 500));
    }
    await page.screenshot({ path: path.join(out, `${tag}-failure.png`) });
    throw new Error('Solvra webview not found');
  }
  const close = async () => {
    fs.writeFileSync(path.join(out, `${tag}-log.json`), JSON.stringify(log, null, 2));
    await browser.close().catch(() => {});
    proc.kill('SIGTERM');
  };
  return { page, step, shot, openSolvra, chatFrame, close, log };
}
