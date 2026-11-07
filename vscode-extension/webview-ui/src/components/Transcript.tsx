import { useEffect, useRef, useState } from 'react';
import { formatCost, formatDuration, formatTokens, type Action, type Item, type State, type SubAgent, type Turn } from '../store';
import { Markdown } from './Markdown';
import { ToolLine } from './ToolLine';
import { ErrorLine } from './ErrorLine';
import { ChipView } from './Composer';
import { Icon } from './Icon';

export function Transcript({ state, dispatch }: { state: State; dispatch: (a: Action) => void }) {
  const end = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  const last = state.turns[state.turns.length - 1];
  // Follow the stream unless the user scrolled up.
  useEffect(() => {
    const el = end.current?.closest('.scroll');
    if (!el) return;
    const onScroll = () => { stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80; };
    el.addEventListener('scroll', onScroll, { passive: true });
    return () => el.removeEventListener('scroll', onScroll);
  }, []);
  useEffect(() => { if (stick.current) end.current?.scrollIntoView({ block: 'end' }); });
  return (
    <div className="transcript">
      {state.turns.map((t) => <TurnView key={t.id} turn={t} live={t === last && state.runId !== null} dispatch={dispatch} />)}
      <div ref={end} />
    </div>
  );
}

function TurnView({ turn, live, dispatch }: { turn: Turn; live: boolean; dispatch: (a: Action) => void }) {
  return (
    <section className="turn">
      {(turn.user.text || turn.user.chips.length > 0) && (
        <div className="user-msg" dir="auto">
          {turn.user.chips.length > 0 && <div className="chips">{turn.user.chips.map((c, i) => <ChipView key={i} chip={c} />)}</div>}
          {turn.user.text && <div className="user-text">{turn.user.text}</div>}
        </div>
      )}
      <AgentItems turn={turn} agent="main" live={live} dispatch={dispatch} />
      {turn.summary && <RunSummary turn={turn} />}
    </section>
  );
}

function AgentItems({ turn, agent, live, dispatch }: { turn: Turn; agent: string; live: boolean; dispatch: (a: Action) => void }) {
  const items = turn.items.filter((i) => ('agent' in i ? i.agent === agent : agent === 'main'));
  const lastText = [...items].reverse().find((i) => i.kind === 'text');
  return (
    <>
      {items.map((item) => {
        if (item.kind === 'tool' && item.name === 'agent') {
          const sub = Object.values(turn.agents).find((a) => a.toolCallId === item.id);
          if (sub) return <SubAgentBlock key={item.key} turn={turn} sub={sub} item={item} live={live} dispatch={dispatch} />;
        }
        return <ItemView key={item.key} item={item} streaming={live && item === lastText} />;
      })}
    </>
  );
}

function ItemView({ item, streaming }: { item: Item; streaming: boolean }) {
  switch (item.kind) {
    case 'text': return <div className="reply"><Markdown text={item.text} streaming={streaming} /></div>;
    case 'thinking': return <Thinking text={item.text} />;
    case 'tool': return <ToolLine item={item} />;
    case 'error': return <ErrorLine message={item.message} fix={item.fix} provider={item.provider} />;
    case 'notice': return <div className="notice" dir="auto">{item.text}</div>;
  }
}

function Thinking({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="thinking">
      <button className="step-line quiet" aria-expanded={open} onClick={() => setOpen(!open)}>
        <Icon name={open ? 'chevron-down' : 'chevron-right'} className="dim chevron" />
        <span className="verb">Thought</span>
      </button>
      {open && <div className="thinking-body" dir="auto">{text}</div>}
    </div>
  );
}

function useElapsed(start: number, end: number | undefined, active: boolean): number {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!active || end) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [active, end]);
  return (end ?? now) - start;
}

function SubAgentBlock({ turn, sub, item, live, dispatch }: { turn: Turn; sub: SubAgent; item: Extract<Item, { kind: 'tool' }>; live: boolean; dispatch: (a: Action) => void }) {
  const [open, setOpen] = useState(true);
  const elapsed = useElapsed(sub.startedAt, sub.endedAt, live);
  const steps = turn.items.filter((i) => 'agent' in i && i.agent === sub.id && i.kind === 'tool').length;
  return (
    <div className={`subagent ${sub.status}`}>
      <button className="step-line" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span className={`dot ${sub.status === 'running' ? 'running' : sub.status === 'ok' ? 'ok' : 'error'}`} aria-hidden="true" />
        <span className="verb">Agent</span>
        <span className="target" dir="auto">{sub.title}</span>
        <span className="meta">{steps > 0 ? `${steps} step${steps === 1 ? '' : 's'} · ` : ''}{formatDuration(elapsed)}{sub.status === 'error' ? ' · failed' : sub.status === 'cancelled' ? ' · stopped' : ''}</span>
        <Icon name={open ? 'chevron-down' : 'chevron-right'} className="dim chevron" />
      </button>
      {open && <div className="subagent-body"><AgentItems turn={turn} agent={sub.id} live={live} dispatch={dispatch} /></div>}
      {!open && item.status === 'error' && item.output && <div className="tool-error" dir="auto">{item.output}</div>}
    </div>
  );
}

function RunSummary({ turn }: { turn: Turn }) {
  const s = turn.summary!;
  const head = s.stopReason === 'cancelled' ? 'Stopped after' : s.stopReason === 'error' ? 'Failed after' : 'Worked for';
  return (
    <div className="run-summary">
      {head} {formatDuration(s.durationMs)} · {formatTokens(s.tokens)} tokens · {formatCost(s.costUsd)}
      {s.stopReason === 'max_turns' && ' · turn limit reached'}
      {s.stopReason === 'max_budget' && ' · budget reached'}
    </div>
  );
}
