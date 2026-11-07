// Writes ../media/v3/THIRD-PARTY-NOTICES.txt: the licence of every production dependency bundled
// into the webview (code, fonts, icons). Run after `vite build`.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const tree = JSON.parse(execFileSync('npm', ['ls', '--omit=dev', '--all', '--json'], { encoding: 'utf8', maxBuffer: 64 << 20 }));
const seen = new Map();
(function walk(deps) {
  for (const [name, info] of Object.entries(deps ?? {})) {
    if (!info.version || seen.has(`${name}@${info.version}`)) continue;
    seen.set(`${name}@${info.version}`, name);
    walk(info.dependencies);
  }
})(tree.dependencies);

const parts = ['Third-party software bundled in the Solvra chat webview (media/v3).\n'];
for (const [id, name] of [...seen].sort()) {
  const dir = path.join('node_modules', name);
  const pkg = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
  const files = fs.readdirSync(dir).filter((f) => /^(licen[cs]e|copying|notice)/i.test(f));
  const text = files.map((f) => fs.readFileSync(path.join(dir, f), 'utf8').trim()).join('\n\n');
  parts.push(`${'='.repeat(78)}\n${id}  (${pkg.license ?? 'see below'})\n${pkg.homepage ?? pkg.repository?.url ?? ''}\n${'-'.repeat(78)}\n${text || `License: ${pkg.license}`}\n`);
}
fs.writeFileSync('../media/v3/THIRD-PARTY-NOTICES.txt', parts.join('\n'));
console.log(`notices: ${seen.size} packages`);
