import { useEffect, useRef, useState } from 'react';
import { motion } from 'motion/react';
import type { PermissionRequestEvent } from '../../../src/protocol';
import type { Action } from '../store';
import { post } from '../vscode';
import { DiffView } from './DiffView';
import { Icon } from './Icon';

const KIND: Record<string, { icon: string; question: string }> = {
  edit: { icon: 'edit', question: 'Make this edit?' },
  execute: { icon: 'terminal', question: 'Run this command?' },
  agent: { icon: 'organization', question: 'Start this agent?' },
  network: { icon: 'globe', question: 'Allow this network access?' },
  read: { icon: 'eye', question: 'Allow this?' },
};

function alwaysText(always: string): string {
  if (always.startsWith('execute:')) return `Yes, and don’t ask again for ${always.slice(8)} commands`;
  return { edit: 'Yes, and don’t ask again for edits', agent: 'Yes, and don’t ask again for agents', network: 'Yes, and don’t ask again for network access', read: 'Yes, and don’t ask again for these' }[always] ?? 'Yes, and don’t ask again';
}

/** Replaces the input box while Solvra waits for an answer. 1 / 2 / 3 or arrows + Enter. */
export function Approval({ request, pending, dispatch }: { request: PermissionRequestEvent; pending: number; dispatch: (a: Action) => void }) {
  type Option = { key: 'allow' | 'allow_always' | 'deny'; label: string };
  const options: Option[] = [
    { key: 'allow', label: 'Yes' },
    ...(request.always ? [{ key: 'allow_always' as const, label: alwaysText(request.always) }] : []),
    { key: 'deny', label: 'No, and tell Solvra what to do instead' },
  ];
  const [active, setActive] = useState(0);
  const [telling, setTelling] = useState(false);
  const [message, setMessage] = useState('');
  const box = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => { box.current?.focus(); }, []);
  useEffect(() => { if (telling) input.current?.focus(); }, [telling]);

  const answer = (decision: 'allow' | 'allow_always' | 'deny', msg?: string) => {
    post({ type: 'permission', id: request.id, decision, message: msg });
    dispatch({ type: 'local/answered', id: request.id, decision });
  };
  const choose = (i: number) => {
    const o = options[i];
    if (o.key === 'deny') { setTelling(true); setActive(i); return; }
    answer(o.key);
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (telling) return;
    const n = Number(e.key);
    if (n >= 1 && n <= options.length) { e.preventDefault(); choose(n - 1); return; }
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive((active + 1) % options.length); }
    if (e.key === 'ArrowUp') { e.preventDefault(); setActive((active - 1 + options.length) % options.length); }
    if (e.key === 'Enter') { e.preventDefault(); choose(active); }
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setTelling(true); setActive(options.length - 1); }
  };

  const kind = KIND[request.kind] ?? KIND.read;
  return (
    <motion.div className="approval" ref={box} tabIndex={-1} onKeyDown={onKey} role="dialog" aria-label={`${kind.question} ${request.summary}`}
      initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }}>
      <div className="approval-head">
        <Icon name={kind.icon} className="dim" />
        <span className="approval-q">{kind.question}</span>
        <span className="ellipsis dim" dir="auto">{request.summary}</span>
        {pending > 1 && <span className="badge">{pending - 1} more waiting</span>}
      </div>
      {request.diff && <div className="approval-preview"><DiffView diff={request.diff} max={200} /></div>}
      {!request.diff && request.command && <pre className="cmd approval-preview"><span className="dim">$ </span>{request.command}</pre>}
      <div className="options" role="listbox" aria-label="Your answer">
        {options.map((o, i) => (
          <button key={o.key} role="option" aria-selected={i === active} className={`option ${i === active ? 'active' : ''}`}
            onMouseEnter={() => !telling && setActive(i)} onClick={() => choose(i)}>
            <span className="num">{i + 1}</span><span>{o.label}</span>
          </button>
        ))}
      </div>
      {telling && (
        <div className="tell">
          <input ref={input} dir="auto" placeholder="What should Solvra do instead? (Enter to send, empty to just say no)" value={message} aria-label="What Solvra should do instead"
            onChange={(e) => setMessage(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') { e.preventDefault(); answer('deny', message.trim() || undefined); }
              if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setTelling(false); box.current?.focus(); }
            }} />
          <button className="btn primary small" onClick={() => answer('deny', message.trim() || undefined)}>Send</button>
        </div>
      )}
      <div className="hint dim">Press 1–{options.length} · ↑↓ and Enter</div>
    </motion.div>
  );
}
