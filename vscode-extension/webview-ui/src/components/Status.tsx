import { useEffect, useState } from 'react';
import type { PlanItem } from '../../../src/protocol';
import { activity, formatDuration, formatTokens, type Turn } from '../store';
import { Icon } from './Icon';

export function PlanList({ items }: { items: PlanItem[] }) {
  const [open, setOpen] = useState(true);
  const done = items.filter((i) => i.status === 'done').length;
  return (
    <div className="plan">
      <button className="plan-head" aria-expanded={open} onClick={() => setOpen(!open)}>
        <Icon name="checklist" className="dim" /><span>Plan</span><span className="dim">{done}/{items.length}</span>
        <span className="grow" /><Icon name={open ? 'chevron-down' : 'chevron-up'} className="dim" />
      </button>
      {open && (
        <ul className="plan-items">
          {items.map((i, k) => (
            <li key={k} className={i.status} dir="auto">
              <span className={`check ${i.status}`} aria-label={i.status === 'done' ? 'done' : i.status === 'active' ? 'in progress' : 'pending'}>
                {i.status === 'done' && <Icon name="check" />}
              </span>
              <span>{i.text}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function StatusLine({ turn, usage, waiting }: { turn: Turn | undefined; usage: { input: number; output: number }; waiting: boolean }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(t); }, []);
  const elapsed = turn ? now - turn.startedAt : 0;
  return (
    <div className="status-line" role="status">
      <span className={`shimmer ${waiting ? 'paused' : ''}`}>{waiting ? 'Waiting for your answer' : activity(turn)}</span>
      <span className="dim">{formatDuration(elapsed)} · {formatTokens(usage.input + usage.output)} tokens</span>
      <span className="grow" />
      {!waiting && <span className="dim"><kbd>esc</kbd> to stop</span>}
    </div>
  );
}
