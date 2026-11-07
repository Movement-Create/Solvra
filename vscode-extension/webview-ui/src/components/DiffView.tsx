/** Unified diff with soft green/red lines and old/new line numbers. */
export function DiffView({ diff, max = 400 }: { diff: string; max?: number }) {
  const rows: { cls: string; a: string; b: string; text: string }[] = [];
  let a = 0, b = 0;
  for (const line of diff.split('\n')) {
    if (line.startsWith('--- ') || line.startsWith('+++ ') || line === '') continue;
    const h = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
    if (h) { a = Number(h[1]); b = Number(h[2]); rows.push({ cls: 'hunk', a: '', b: '', text: line }); continue; }
    if (line.startsWith('+')) rows.push({ cls: 'add', a: '', b: String(b++), text: line.slice(1) });
    else if (line.startsWith('-')) rows.push({ cls: 'del', a: String(a++), b: '', text: line.slice(1) });
    else rows.push({ cls: 'ctx', a: String(a++), b: String(b++), text: line.slice(1) });
  }
  const shown = rows.slice(0, max);
  return (
    <div className="diff" role="table" aria-label="Changes">
      {shown.map((r, i) => (
        <div key={i} className={`diff-row ${r.cls}`} role="row">
          <span className="ln" role="cell">{r.a}</span>
          <span className="ln" role="cell">{r.b}</span>
          <span className="sign" aria-hidden="true">{r.cls === 'add' ? '+' : r.cls === 'del' ? '−' : ''}</span>
          <span className="code" role="cell">{r.text || ' '}</span>
        </div>
      ))}
      {rows.length > max && <div className="diff-row hunk">… {rows.length - max} more lines</div>}
    </div>
  );
}
