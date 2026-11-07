import { useEffect, useState } from 'react';
import type { State } from '../store';
import { post } from '../vscode';
import { ViewHeader } from './Settings';
import { Icon } from './Icon';

export function MemoryView({ state, onBack }: { state: State; onBack: () => void }) {
  const [fact, setFact] = useState('');
  const [query, setQuery] = useState('');
  const m = state.memory;
  return (
    <div className="view scroll" onKeyDown={(e) => { if (e.key === 'Escape') onBack(); }}>
      <ViewHeader title="Memory" onBack={onBack} />
      <p className="dim small pad">Facts Solvra keeps between chats, such as project conventions or your preferences.</p>
      <section className="settings-section">
        <h3>Remember something</h3>
        <div className="row">
          <input className="grow" dir="auto" placeholder="e.g. Tests live in tests/ and run with npm test" value={fact} onChange={(e) => setFact(e.target.value)} aria-label="Fact to remember"
            onKeyDown={(e) => { if (e.key === 'Enter' && fact.trim()) { post({ type: 'memoryAdd', text: fact }); setFact(''); } }} />
          <button className="btn primary small" disabled={!fact.trim()} onClick={() => { post({ type: 'memoryAdd', text: fact }); setFact(''); }}>Remember</button>
        </div>
        {m?.added && <div className="dim small">Saved: “{m.results[0]}”</div>}
      </section>
      <section className="settings-section">
        <h3>Search</h3>
        <div className="row">
          <input className="grow" dir="auto" placeholder="Search memory" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Search memory"
            onKeyDown={(e) => { if (e.key === 'Enter' && query.trim()) post({ type: 'memorySearch', query }); }} />
          <button className="btn ghost small" disabled={!query.trim()} onClick={() => post({ type: 'memorySearch', query })}>Search</button>
        </div>
        {m && !m.added && m.query && (
          m.results.length === 0
            ? <div className="dim small">Nothing found for “{m.query}”.</div>
            : <ul className="memory-results">{m.results.map((r, i) => <li key={i} dir="auto">{r}</li>)}</ul>
        )}
      </section>
    </div>
  );
}

const KIND_LABEL: Record<string, string> = { read: 'Reads (no approval needed)', network: 'Network', edit: 'Edits (asks in Ask mode)', execute: 'Commands (asks in Ask mode)', agent: 'Agents (asks in Ask mode)' };

export function ToolsView({ state, onBack }: { state: State; onBack: () => void }) {
  useEffect(() => { if (!state.tools) post({ type: 'listTools' }); }, [state.tools]);
  const [open, setOpen] = useState<string | null>(null);
  const tools = state.tools ?? [];
  const kinds = ['read', 'network', 'edit', 'execute', 'agent'].filter((k) => tools.some((t) => t.kind === k));
  return (
    <div className="view scroll" onKeyDown={(e) => { if (e.key === 'Escape') onBack(); }}>
      <ViewHeader title="Tools" onBack={onBack} />
      {!state.tools && <p className="dim pad">Loading…</p>}
      {kinds.map((k) => (
        <section key={k} className="settings-section">
          <h3>{KIND_LABEL[k] ?? k}</h3>
          {tools.filter((t) => t.kind === k).map((t) => (
            <div key={t.name} className="tool-info">
              <button className="step-line" aria-expanded={open === t.name} onClick={() => setOpen(open === t.name ? null : t.name)}>
                <span className="mono">{t.name}</span>
                <span className="dim ellipsis grow">{t.description.split('. ')[0]}</span>
                <Icon name={open === t.name ? 'chevron-down' : 'chevron-right'} className="dim chevron" />
              </button>
              {open === t.name && <p className="small dim tool-desc">{t.description}</p>}
            </div>
          ))}
        </section>
      ))}
    </div>
  );
}
