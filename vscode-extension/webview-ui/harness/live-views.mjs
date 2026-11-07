// A run started in the sidebar and opened in an editor tab while it is working must show live in
// both, answer from either, and never start a second run. Usage: node harness/live-views.mjs [outDir]
import fs from 'node:fs';
import path from 'node:path';
import { launch } from './vscode-launch.mjs';

const out = path.resolve(process.argv[2] || '/tmp/sv-live-views');
const work = '/tmp/svdemo', sessions = '/tmp/svsessions-views';
fs.rmSync(work, { recursive: true, force: true }); fs.mkdirSync(work, { recursive: true });
fs.rmSync(sessions, { recursive: true, force: true });
fs.writeFileSync(path.join(work, 'notes.txt'), 'line one\nline two\n');
const { page, step, shot, openSolvra, chatFrame, close } = await launch({ out, tag: 'views', open: work, sessions, settings: { 'workbench.colorTheme': 'Default Dark Modern' } });

async function frameFor(surface) {
  for (let i = 0; i < 60; i++) {
    for (const f of page.frames()) {
      try { if (await f.evaluate((s) => document.body?.dataset.surface === s && !!document.querySelector('.app'), surface)) return f; } catch { /* detached */ }
    }
    await page.waitForTimeout(500);
  }
  throw new Error(`no ${surface} chat`);
}

try {
  await openSolvra();
  await chatFrame();
  const side = await frameFor('sidebar');
  await side.fill('textarea[aria-label="Message Solvra"]', 'Use file_edit to change "line two" to "line 2" in notes.txt. Nothing else.');
  await side.press('textarea[aria-label="Message Solvra"]', 'Enter');
  await side.waitForSelector('.approval', { timeout: 180000 });
  step('sidebar: waiting for approval');

  await side.click('button[aria-label="Open in editor tab"]');
  const tab = await frameFor('tab');
  await tab.waitForSelector('.approval', { timeout: 20000 });
  const status = await tab.locator('.status-line').innerText();
  step(`tab shows the live run: approval + status "${status.replace(/\s+/g, ' ').trim()}"`);
  step(`tab cannot send a second message: ${(await tab.locator('textarea[aria-label="Message Solvra"]').count()) === 0}`);
  await page.waitForTimeout(500);
  await shot('01-tab-live-approval');

  await tab.press('.approval', '1');
  await page.waitForTimeout(1500);
  step(`sidebar approval gone after answering in the tab: ${(await side.locator('.approval').count()) === 0}`);

  await tab.waitForSelector('.run-summary', { timeout: 180000 });
  await side.waitForSelector('.run-summary', { timeout: 30000 });
  step(`both views finished; notes.txt: ${JSON.stringify(fs.readFileSync(path.join(work, 'notes.txt'), 'utf8'))}`);
  step(`session files: ${fs.readdirSync(sessions).filter((f) => f.endsWith('.jsonl') && f !== 'cost-ledger.jsonl').length}`);
  step(`tab turns: ${await tab.locator('.user-msg').count()}, sidebar turns: ${await side.locator('.user-msg').count()}`);
  await shot('02-both-finished');

  // The / and @ menus in the real sidebar and tab: fully visible, not clipped.
  const visible = (f) => f.evaluate(() => {
    const popup = document.querySelector('.popup');
    if (!popup) return 'no menu';
    const items = [...popup.querySelectorAll('.popup-item')];
    const shown = (el) => { const b = el.getBoundingClientRect(); const hit = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2); return !!hit && el.contains(hit); };
    return items.length && shown(items[0]) && shown(items[items.length - 1]) ? 'fully visible' : `clipped (${items.length} items)`;
  });
  for (const [name, f] of [['tab', tab], ['sidebar', side]]) {
    await f.fill('textarea[aria-label="Message Solvra"]', '/');
    await page.waitForTimeout(400);
    step(`${name} / menu: ${await visible(f)}`);
    if (name === 'tab') await shot('03-tab-slash-menu');
    await f.fill('textarea[aria-label="Message Solvra"]', '');
    await f.type('textarea[aria-label="Message Solvra"]', 'see @note');
    await page.waitForTimeout(1200);
    step(`${name} @ menu: ${await visible(f)}`);
    if (name === 'sidebar') await shot('04-sidebar-mention-menu');
    await f.fill('textarea[aria-label="Message Solvra"]', '');
  }
} catch (e) {
  await shot('failure').catch(() => {});
  throw e;
} finally {
  await close();
}
