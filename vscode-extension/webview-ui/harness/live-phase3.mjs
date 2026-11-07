// Live Phase 3 checks (throwaway multi-root workspace, auto mode, low budget):
// native diff review with Undo from the editor title bar, restore-to-message, every workspace
// folder as context, an editor right-click action as a chip, and file links.
//   VSCODE=/path/to/code node harness/live-phase3.mjs <outDir>
import fs from 'node:fs';
import path from 'node:path';
import { launch } from './vscode-launch.mjs';

const out = path.resolve(process.argv[2] || '/tmp/sv-live3');
const app = '/tmp/svdemo', data = '/tmp/svdata';
for (const d of [app, data]) { fs.rmSync(d, { recursive: true, force: true }); fs.mkdirSync(d, { recursive: true }); }
const original = 'const items = [];\nconst total = items.reduce((a, b) => a + b);\nconsole.log(total);\n';
fs.writeFileSync(path.join(app, 'sample.js'), original);
fs.writeFileSync(path.join(app, 'notes.txt'), 'line one\nline two\n');
fs.writeFileSync(path.join(data, 'rows.csv'), 'id,name\n1,a\n2,b\n3,c\n4,d\n5,e\n');
const ws = '/tmp/svmulti.code-workspace';
fs.writeFileSync(ws, JSON.stringify({ folders: [{ path: app }, { path: data }] }));

const { page, step, shot, openSolvra, chatFrame, close } = await launch({ out, tag: 'p3', open: ws, settings: { 'workbench.colorTheme': 'Default Dark Modern', 'solvra.autoApprove': true } });
const input = 'textarea[aria-label="Message Solvra"]';
try {
  await openSolvra();
  let f = await chatFrame();
  const send = async (text) => { await f.fill(input, text); await f.press(input, 'Enter'); };

  // 1. Two workspace folders: the second one must be usable as context.
  await send('Fix sample.js so an empty list prints 0 (use file_edit). Then tell me how many data rows rows.csv in the svdata workspace folder has (not counting the header).');
  await f.waitForSelector('.run-summary', { timeout: 240000 });
  await page.waitForTimeout(500);
  const reply = await f.locator('.reply').last().innerText();
  step(`reply mentions 5 rows: ${/\b5\b|five/i.test(reply)}`);
  step(`sample.js edited: ${fs.readFileSync(path.join(app, 'sample.js'), 'utf8').includes(', 0)')}`);
  await shot('01-multi-root-done');

  // 2. Review in VS Code's diff editor; Undo from the editor title bar restores the file.
  await f.locator('.edit-actions button', { hasText: 'Review' }).first().click();
  await page.waitForSelector('.tab.active[aria-label*="Solvra"]', { timeout: 15000 }).catch(() => {});
  await page.waitForTimeout(1500);
  await shot('02-native-diff');
  const undoBtn = page.locator('.editor-actions [aria-label^="Undo Solvra"]');
  step(`editor title has Keep/Undo: ${(await page.locator('.editor-actions [aria-label^="Keep Solvra"]').count()) > 0 && (await undoBtn.count()) > 0}`);
  await undoBtn.first().click();
  await page.waitForTimeout(1200);
  step(`undo from editor restored the original: ${fs.readFileSync(path.join(app, 'sample.js'), 'utf8') === original}`);
  f = await chatFrame();
  await page.waitForTimeout(500);
  await shot('03-after-editor-undo');

  // 3. Checkpoints: two edits in one message, then "Restore files to before this message".
  await send('Make two edits with file_edit: in sample.js change "const items = [];" to "const items = [1, 2];", and in notes.txt change "line two" to "line 2".');
  await f.waitForSelector('.run-summary >> nth=1', { timeout: 240000 });
  await page.waitForTimeout(500);
  const changed = fs.readFileSync(path.join(app, 'sample.js'), 'utf8') !== original && fs.readFileSync(path.join(app, 'notes.txt'), 'utf8').includes('line 2');
  step(`second message edited both files: ${changed}`);
  await f.locator('.restore-link').last().click();
  await shot('04-restore-confirm');
  await f.locator('.confirm-inline button', { hasText: 'Restore' }).click();
  await page.waitForTimeout(1200);
  step(`restore put both files back: ${fs.readFileSync(path.join(app, 'sample.js'), 'utf8') === original && fs.readFileSync(path.join(app, 'notes.txt'), 'utf8') === 'line one\nline two\n'}`);
  await shot('05-after-restore');

  // 4. File link in a reply opens the file at the line.
  await send('Reply with exactly this and nothing else: The bug is on `sample.js:2`.');
  await f.waitForSelector('.run-summary >> nth=2', { timeout: 240000 });
  await f.locator('.file-link').last().click();
  await page.waitForTimeout(1500);
  const activeTab = await page.locator('.tab.active').first().getAttribute('aria-label');
  const cursor = await page.locator('.statusbar [aria-label^="Ln "], .statusbar-item [aria-label*="Ln "]').first().innerText().catch(() => '');
  step(`file link opened: ${activeTab} ${cursor}`);
  await shot('06-file-link');

  // 5. Editor right-click "Explain with Solvra" sends the selection as a chip.
  await page.click('.monaco-editor .view-lines');
  await page.keyboard.press('Control+Home');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('Shift+ArrowDown');
  // Right-click on the selected text (a click elsewhere would move the cursor and drop the selection).
  await page.locator('.monaco-editor .view-line').first().click({ button: 'right', position: { x: 20, y: 8 } });
  await page.waitForSelector('.context-view .monaco-menu', { timeout: 10000 });
  await shot('07-editor-context-menu');
  await page.locator('.context-view .action-label', { hasText: 'Explain with Solvra' }).click();
  f = await chatFrame();
  await f.waitForSelector('.user-msg .chip', { timeout: 30000 });
  const chip = await f.locator('.user-msg .chip').last().innerText();
  step(`explain sent the selection as a chip: ${chip.trim()}`);
  await f.waitForSelector('.run-summary >> nth=3', { timeout: 240000 });
  await shot('08-explain-done');
} catch (e) {
  await shot('failure').catch(() => {});
  throw e;
} finally {
  await close();
}
