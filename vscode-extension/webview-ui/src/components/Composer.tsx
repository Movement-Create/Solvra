import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import type { ChatMode, Chip, ProviderGroup } from '../../../src/protocol';
import { autoChip, isRunning, MODES, nextMode, type Action, type State, type View } from '../store';
import { post } from '../vscode';
import { Icon } from './Icon';

export const PLACEHOLDER = 'Ask Solvra anything, @ to add files, / for commands';

const MODE_TEXT: Record<ChatMode, { label: string; title: string; icon: string }> = {
  ask: { label: 'Ask before edits', title: 'Solvra asks before it edits files, runs commands or starts agents', icon: 'shield' },
  plan: { label: 'Plan only', title: 'Solvra reads and plans; it changes nothing', icon: 'checklist' },
  auto: { label: 'Auto-approve', title: 'Solvra edits files and runs commands without asking', icon: 'zap' },
};

interface Command { name: string; description: string; run: () => void }

export function ChipView({ chip, onRemove, auto }: { chip: Chip; onRemove?: () => void; auto?: boolean }) {
  const icon = chip.kind === 'folder' ? 'folder' : chip.kind === 'selection' ? 'selection' : 'file';
  return (
    <span className={`chip ${auto ? 'auto' : ''}`} title={auto ? `Current selection: ${chip.path} (attached automatically)` : chip.path}>
      <Icon name={icon} className="dim" />
      <span className="ellipsis">{chip.label}</span>
      {onRemove && <button className="chip-x" aria-label={`Remove ${chip.label}`} onClick={onRemove}><Icon name="close" /></button>}
    </span>
  );
}

export function Composer({ state, dispatch, onView }: { state: State; dispatch: (a: Action) => void; onView: (v: View) => void }) {
  const [text, setText] = useState('');
  const [popup, setPopup] = useState<null | { kind: 'mention' | 'slash'; query: string; start: number }>(null);
  const [active, setActive] = useState(0);
  const [modelOpen, setModelOpen] = useState(false);
  const ta = useRef<HTMLTextAreaElement>(null);
  const running = isRunning(state);
  const selection = autoChip(state);

  // Auto-size the input.
  useLayoutEffect(() => {
    const el = ta.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 220)}px`;
  }, [text]);

  useEffect(() => { ta.current?.focus(); }, []);
  useEffect(() => { if (state.openRequest?.view === 'model') setModelOpen(true); }, [state.openRequest]);

  // Prefill from editor actions and suggestions.
  useEffect(() => {
    const d = state.draft;
    if (!d) return;
    if (d.text) setText(d.text);
    ta.current?.focus();
    if (d.send && d.text) send(d.text);
  }, [state.draft?.nonce]); // eslint-disable-line react-hooks/exhaustive-deps

  // Retry button on errors: resend the last message.
  useEffect(() => {
    const onRetry = () => {
      const last = state.turns[state.turns.length - 1];
      if (last && !running) post({ type: 'send', text: last.user.text, chips: last.user.chips, mode: state.mode, model: state.model });
    };
    window.addEventListener('solvra:retry', onRetry);
    return () => window.removeEventListener('solvra:retry', onRetry);
  });

  function send(value = text) {
    const chips = selection ? [...state.chips, selection] : state.chips;
    if (running || (!value.trim() && chips.length === 0)) return;
    post({ type: 'send', text: value.trim(), chips, mode: state.mode, model: state.model });
    setText('');
    setPopup(null);
  }

  const setMode = (mode: ChatMode) => { dispatch({ type: 'local/setMode', mode }); post({ type: 'setMode', mode }); };

  const commands: Command[] = useMemo(() => [
    { name: 'new', description: 'Start a new chat', run: () => { post({ type: 'newChat' }); dispatch({ type: 'newChat' }); } },
    { name: 'model', description: 'Choose the model', run: () => setModelOpen(true) },
    { name: 'mode', description: `Switch mode (now: ${MODE_TEXT[state.mode].label})`, run: () => setMode(nextMode(state.mode)) },
    { name: 'resume', description: 'Open an earlier session', run: () => dispatch({ type: 'open', view: 'sessions' }) },
    { name: 'memory', description: 'Add to or search Solvra’s memory', run: () => onView('memory') },
    { name: 'tools', description: 'See the tools Solvra can use', run: () => { post({ type: 'listTools' }); onView('tools'); } },
    { name: 'settings', description: 'Keys, default model, limits and more', run: () => onView('settings') },
  ], [state.mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const slashItems = popup?.kind === 'slash' ? commands.filter((c) => c.name.startsWith(popup.query.toLowerCase())) : [];
  const mentionItems = popup?.kind === 'mention' ? state.files.items : [];
  const itemCount = popup?.kind === 'slash' ? slashItems.length : mentionItems.length;

  function onChange(value: string, caret: number) {
    setText(value);
    const before = value.slice(0, caret);
    const slash = /^\/(\w*)$/.exec(before);
    const at = /(^|\s)@([^\s@]*)$/.exec(before);
    if (slash) { setPopup({ kind: 'slash', query: slash[1], start: 0 }); setActive(0); }
    else if (at) {
      const query = at[2];
      setPopup({ kind: 'mention', query, start: caret - query.length - 1 });
      setActive(0);
      post({ type: 'searchFiles', query });
    } else setPopup(null);
  }

  function choose(index: number) {
    if (!popup) return;
    if (popup.kind === 'slash') {
      const cmd = slashItems[index];
      if (!cmd) return;
      setText('');
      setPopup(null);
      cmd.run();
      return;
    }
    const item = mentionItems[index];
    if (!item) return;
    dispatch({ type: 'local/addChip', chip: { kind: item.kind, path: item.path, label: item.label } });
    const caret = ta.current?.selectionStart ?? text.length;
    setText(text.slice(0, popup.start) + text.slice(caret));
    setPopup(null);
    ta.current?.focus();
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (popup && itemCount > 0) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setActive((active + 1) % itemCount); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setActive((active - 1 + itemCount) % itemCount); return; }
      if (e.key === 'Enter' || (e.key === 'Tab' && !e.shiftKey)) { e.preventDefault(); choose(active); return; }
    }
    if (popup && e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setPopup(null); return; }
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); send(); return; }
    if (e.key === 'Backspace' && !text && state.chips.length) dispatch({ type: 'local/removeChip', index: state.chips.length - 1 });
  }

  return (
    <div className="composer">
      {popup && itemCount > 0 && (
        <div className="popup" role="listbox" aria-label={popup.kind === 'slash' ? 'Commands' : 'Files and folders'}>
          {popup.kind === 'slash'
            ? slashItems.map((c, i) => (
                <button key={c.name} role="option" aria-selected={i === active} className={`popup-item ${i === active ? 'active' : ''}`} onMouseEnter={() => setActive(i)} onMouseDown={(e) => { e.preventDefault(); choose(i); }}>
                  <span className="mono">/{c.name}</span><span className="dim ellipsis">{c.description}</span>
                </button>
              ))
            : mentionItems.map((f, i) => (
                <button key={f.path} role="option" aria-selected={i === active} className={`popup-item ${i === active ? 'active' : ''}`} onMouseEnter={() => setActive(i)} onMouseDown={(e) => { e.preventDefault(); choose(i); }}>
                  <Icon name={f.kind === 'folder' ? 'folder' : 'file'} className="dim" />
                  <span className="ellipsis">{f.label}</span><span className="dim ellipsis small">{f.path}</span>
                </button>
              ))}
        </div>
      )}
      <div className="input-box">
        {(state.chips.length > 0 || selection) && (
          <div className="chips">
            {state.chips.map((c, i) => <ChipView key={`${c.path}:${c.startLine ?? ''}:${i}`} chip={c} onRemove={() => dispatch({ type: 'local/removeChip', index: i })} />)}
            {selection && <ChipView chip={selection} auto onRemove={() => dispatch({ type: 'local/dismissSelection' })} />}
          </div>
        )}
        <textarea
          ref={ta}
          rows={1}
          dir="auto"
          value={text}
          placeholder={PLACEHOLDER}
          aria-label="Message Solvra"
          onChange={(e) => onChange(e.target.value, e.target.selectionStart)}
          onKeyDown={onKeyDown}
          onBlur={() => setTimeout(() => setPopup(null), 120)}
        />
        <div className="toolbar">
          <button className={`pill mode-${state.mode}`} title={`${MODE_TEXT[state.mode].title}. Click or press ⇧Tab to switch.`} aria-label={`Mode: ${MODE_TEXT[state.mode].label}. Switch mode`} onClick={() => setMode(nextMode(state.mode))}>
            <Icon name={MODE_TEXT[state.mode].icon} /><span>{MODE_TEXT[state.mode].label}</span>
          </button>
          <ModelPicker state={state} dispatch={dispatch} open={modelOpen} setOpen={setModelOpen} onSettings={() => onView('settings')} />
          <span className="grow" />
          <ContextRing pct={state.usage.contextPct} />
          {running
            ? <button className="send stop" title="Stop (esc)" aria-label="Stop" onClick={() => post({ type: 'cancel' })}><Icon name="debug-stop" /></button>
            : <button className="send" title="Send (Enter)" aria-label="Send" disabled={!text.trim() && !state.chips.length && !selection} onClick={() => send()}><Icon name="arrow-up" /></button>}
        </div>
      </div>
    </div>
  );
}

export { MODES };

function ContextRing({ pct }: { pct: number }) {
  const r = 7, c = 2 * Math.PI * r;
  const p = Math.max(0, Math.min(100, pct || 0));
  return (
    <span className="context-ring" title={`${p}% of the model’s context used`} aria-label={`${p}% of context used`} role="img">
      <svg width="18" height="18" viewBox="0 0 18 18">
        <circle cx="9" cy="9" r={r} className="ring-bg" />
        <circle cx="9" cy="9" r={r} className="ring-fg" strokeDasharray={c} strokeDashoffset={c * (1 - p / 100)} transform="rotate(-90 9 9)" />
      </svg>
    </span>
  );
}

function shortModel(id: string): string {
  const i = id.indexOf(':');
  return i > 0 ? id.slice(i + 1) : id;
}

function ModelPicker({ state, dispatch, open, setOpen, onSettings }: { state: State; dispatch: (a: Action) => void; open: boolean; setOpen: (o: boolean) => void; onSettings: () => void }) {
  const [query, setQuery] = useState('');
  const [keyFor, setKeyFor] = useState<string | null>(null);
  useEffect(() => { if (open) post({ type: 'refreshProviders' }); else { setQuery(''); setKeyFor(null); } }, [open]);
  const q = query.trim().toLowerCase();
  const pick = (id: string) => { dispatch({ type: 'local/setModel', model: id }); post({ type: 'setModel', model: id }); setOpen(false); };
  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button className="pill" title={`Model: ${state.model}`} aria-label={`Model: ${state.model}. Choose model`}>
          <span className="ellipsis">{shortModel(state.model) || 'Model'}</span><Icon name="chevron-down" className="dim" />
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content className="popover model-menu" side="top" align="start" sideOffset={6} collisionPadding={8}>
          <div className="menu-search">
            <Icon name="search" className="dim" />
            <input autoFocus placeholder="Search models or type provider:model" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Search models"
              onKeyDown={(e) => { if (e.key === 'Enter' && query.includes(':')) pick(query.trim()); }} />
          </div>
          <div className="menu-list">
            {state.providers.map((g) => {
              const models = g.models.filter((m) => !q || m.id.toLowerCase().includes(q) || g.label.toLowerCase().includes(q));
              if (q && models.length === 0) return null;
              return (
                <div key={g.provider} className="model-group">
                  <div className="menu-group with-status">
                    <span>{g.label}</span>
                    <ProviderStatus group={g} onAddKey={() => setKeyFor(g.provider)} />
                  </div>
                  {keyFor === g.provider && <InlineKey provider={g.provider} label={g.label} onDone={() => setKeyFor(null)} />}
                  {models.map((m) => (
                    <button key={m.id} className={`menu-item model-item ${m.id === state.model ? 'current' : ''} ${g.ready ? '' : 'unavailable'}`}
                      onClick={() => (g.ready ? pick(m.id) : g.auth === 'key' ? setKeyFor(g.provider) : post({ type: 'runFix', fix: 'codex_login' }))}>
                      <span className="ellipsis mono">{m.label}</span>
                      {m.id === state.model && <Icon name="check" />}
                    </button>
                  ))}
                </div>
              );
            })}
            {q.includes(':') && <button className="menu-item" onClick={() => pick(query.trim())}>Use <span className="mono">{query.trim()}</span></button>}
          </div>
          <button className="menu-footer" onClick={() => { setOpen(false); onSettings(); }}><Icon name="key" /> Manage keys and defaults</button>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function ProviderStatus({ group, onAddKey }: { group: ProviderGroup; onAddKey: () => void }) {
  if (group.ready) return <span className="status ready"><span className="dot ok" />{group.auth === 'none' ? 'Local' : group.auth === 'codex' ? 'Signed in' : 'Key set'}</span>;
  if (group.auth === 'codex') return <button className="link-btn" onClick={() => post({ type: 'runFix', fix: 'codex_login' })}>Sign in</button>;
  return <button className="link-btn" onClick={onAddKey}>Add key</button>;
}

export function InlineKey({ provider, label, onDone }: { provider: string; label: string; onDone: () => void }) {
  const [value, setValue] = useState('');
  const save = () => { if (value.trim()) { post({ type: 'setApiKey', provider, key: value.trim() }); onDone(); } };
  return (
    <div className="inline-key">
      <input type="password" autoFocus placeholder={`${label} API key`} value={value} aria-label={`${label} API key`}
        onChange={(e) => setValue(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') save(); if (e.key === 'Escape') { e.stopPropagation(); onDone(); } }} />
      <button className="btn primary small" disabled={!value.trim()} onClick={save}>Save</button>
    </div>
  );
}
