import { useEffect, useRef, useState } from 'react';
import { editsFrom, formatCost, formatDuration, formatTokens, type Action, type Item, type State, type SubAgent, type Turn } from '../store';
import type { EditInfo } from '../../../src/protocol';
import { post } from '../vscode';
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
      {state.turns.map((t, i) => <TurnView key={t.id} turn={t} live={t === last && state.runId !== null} dispatch={dispatch} edits={state.edits}
        restorable={state.runId === null ? editsFrom(state, i) : []} />)}
      <div ref={end} />
    </div>
  );
}

function TurnView({ turn, live, dispatch, edits, restorable }: { turn: Turn; live: boolean; dispatch: (a: Action) => void; edits: Record<string, EditInfo>; restorable: string[] }) {
  return (
    <section className="turn">
      {(turn.user.text || turn.user.chips.length > 0) && (
        <div className="user-msg" dir="auto">
          {turn.user.chips.length > 0 && <div className="chips">{turn.user.chips.map((c, i) => <ChipView key={i} chip={c} />)}</div>}
          {turn.user.text && <div className="user-text">{turn.user.text}</div>}
        </div>
      )}
      <AgentItems turn={turn} agent="main" live={live} dispatch={dispatch} edits={edits} />
      {turn.summary && <RunSummary turn={turn} />}
      {restorable.length > 0 && turn.items.some((i) => i.kind === 'tool' && restorable.includes(i.id)) && <Restore ids={restorable} edits={edits} />}
    </section>
  );
}

function AgentItems({ turn, agent, live, dispatch, edits }: { turn: Turn; agent: string; live: boolean; dispatch: (a: Action) => void; edits: Record<string, EditInfo> }) {
  const items = turn.items.filter((i) => ('agent' in i ? i.agent === agent : agent === 'main'));
  const lastText = [...items].reverse().find((i) => i.kind === 'text');
  return (
    <>
      {items.map((item) => {
        if (item.kind === 'tool' && item.name === 'agent') {
          const sub = Object.values(turn.agents).find((a) => a.toolCallId === item.id);
          if (sub) return <SubAgentBlock key={item.key} turn={turn} sub={sub} item={item} live={live} dispatch={dispatch} edits={edits} />;
        }
        return <ItemView key={item.key} item={item} streaming={live && item === lastText} edit={item.kind === 'tool' ? edits[item.id] : undefined} />;
      })}
    </>
  );
}

function ItemView({ item, streaming, edit }: { item: Item; streaming: boolean; edit?: EditInfo }) {
  switch (item.kind) {
    case 'text': return <div className="reply"><Markdown text={item.text} streaming={streaming} /></div>;
    case 'thinking': return <Thinking text={item.text} />;
    case 'tool': return <ToolLine item={item} edit={edit} />;
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

function SubAgentBlock({ turn, sub, item, live, dispatch, edits }: { turn: Turn; sub: SubAgent; item: Extract<Item, { kind: 'tool' }>; live: boolean; dispatch: (a: Action) => void; edits: Record<string, EditInfo> }) {
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
      {open && <div className="subagent-body"><AgentItems turn={turn} agent={sub.id} live={live} dispatch={dispatch} edits={edits} /></div>}
      {!open && item.status === 'error' && item.output && <div className="tool-error" dir="auto">{item.output}</div>}
    </div>
  );
}

/** Checkpoint: undo every edit from this message on, with an in-panel confirmation. */
function Restore({ ids, edits }: { ids: string[]; edits: Record<string, EditInfo> }) {
  const [confirm, setConfirm] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => { if (confirm) box.current?.scrollIntoView({ block: 'nearest' }); }, [confirm]);
  const files = new Set(ids.map((id) => edits[id]?.file).filter(Boolean)).size;
  if (!confirm) return <button className="restore-link" onClick={() => setConfirm(true)}><Icon name="history" /> Restore files to before this message</button>;
  return (
    <div className="confirm-inline" role="alertdialog" aria-label="Restore files" ref={box}>
      <span className="grow">Undo {ids.length} edit{ids.length === 1 ? '' : 's'} in {files} file{files === 1 ? '' : 's'} made from here on?</span>
      <button className="btn danger small" onClick={() => { setConfirm(false); post({ type: 'undoEdits', toolIds: ids }); }}>Restore</button>
      <button className="btn ghost small" onClick={() => setConfirm(false)}>Cancel</button>
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
