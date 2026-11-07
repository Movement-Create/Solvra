// Installs a packaged VSIX into a clean profile (no repository settings) and checks that it finds
// its bundled backend and answers through ChatGPT. Usage: node harness/live-vsix.mjs <vsix> [outDir]
import fs from 'node:fs';
import path from 'node:path';
import { launch } from './vscode-launch.mjs';

const vsix = path.resolve(process.argv[2]);
const out = path.resolve(process.argv[3] || '/tmp/sv-live-vsix');
const work = '/tmp/svdemo';
fs.rmSync(work, { recursive: true, force: true });
fs.mkdirSync(work, { recursive: true });
fs.writeFileSync(path.join(work, 'notes.txt'), 'line one\nline two\n');
const { page, step, shot, openSolvra, chatFrame, close } = await launch({ out, tag: 'vsix', open: work, vsix, settings: { 'workbench.colorTheme': 'Default Light Modern' } });
try {
  await openSolvra();
  const f = await chatFrame();
  await shot('01-empty');
  const model = await f.locator('button[aria-label^="Model:"]').getAttribute('aria-label');
  step(`model pill: ${model}`);
  await f.fill('textarea[aria-label="Message Solvra"]', 'How many lines does notes.txt have? Answer with just the number.');
  await f.press('textarea[aria-label="Message Solvra"]', 'Enter');
  // Ask mode is the default: approve whatever the model wants to run, until it finishes.
  for (let i = 0; i < 90 && !(await f.locator('.run-summary').count()); i++) {
    if (await f.locator('.approval').count()) { step('approved a step'); await f.press('.approval', '1'); }
    await page.waitForTimeout(2000);
  }
  await f.waitForSelector('.run-summary', { timeout: 5000 });
  const reply = await f.locator('.reply').last().innerText();
  step(`reply: ${reply.trim()}`);
  const errors = await f.locator('.error-line').count();
  step(`errors in chat: ${errors}`);
  await shot('02-answered');
} catch (e) {
  await shot('failure').catch(() => {});
  throw e;
} finally {
  await close();
}
