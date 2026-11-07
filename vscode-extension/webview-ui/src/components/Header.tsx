import { useEffect, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { groupSessions, type Action, type State } from '../store';
import { post } from '../vscode';
import { Icon } from './Icon';

export function Header({ state, dispatch, onSettings }: { state: State; dispatch: (a: Action) => void; onSettings: () => void }) {
  const [open, setOpen] = useState(false);
  useEffect(() => { if (state.openRequest?.view === 'sessions') setOpen(true); }, [state.openRequest]);
  useEffect(() => { if (open) post({ type: 'refreshSessions' }); }, [open]);
  const newChat = () => { post({ type: 'newChat' }); dispatch({ type: 'newChat' }); };

  return (
    <header className="header">
      <Popover.Root open={open} onOpenChange={setOpen}>
        <Popover.Trigger asChild>
          <button className="session-trigger" title="Sessions" aria-label={`Session: ${state.title}. Open session menu`}>
            <span className="session-title" dir="auto">{state.turns.length || state.sessionId ? state.title : 'New chat'}</span>
            <Icon name="chevron-down" className="dim" />
          </button>
        </Popover.Trigger>
        <Popover.Portal>
          <Popover.Content className="popover session-menu" align="start" sideOffset={6} collisionPadding={8}>
            <SessionMenu state={state} onClose={() => setOpen(false)} onNew={() => { newChat(); setOpen(false); }} />
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
      <div className="header-actions">
        <button className="icon-btn" title="New chat" aria-label="New chat" onClick={newChat}><Icon name="add" /></button>
        {!state.inTab && <button className="icon-btn" title="Open in editor tab" aria-label="Open in editor tab" onClick={() => post({ type: 'openInTab' })}><Icon name="link-external" /></button>}
        <button className="icon-btn" title="Settings" aria-label="Settings" onClick={onSettings}><Icon name="settings-gear" /></button>
      </div>
    </header>
  );
}

function SessionMenu({ state, onClose, onNew }: { state: State; onClose: () => void; onNew: () => void }) {
  const [query, setQuery] = useState('');
  const [editing, setEditing] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const groups = groupSessions(state.sessions, query);
  return (
    <div className="menu-body">
      <div className="menu-search">
        <Icon name="search" className="dim" />
        <input autoFocus placeholder="Search sessions" value={query} onChange={(e) => setQuery(e.target.value)} dir="auto" aria-label="Search sessions" />
      </div>
      <div className="menu-list" role="listbox" aria-label="Sessions">
        {groups.length === 0 && <div className="menu-empty">{query ? 'No sessions match.' : 'No sessions yet.'}</div>}
        {groups.map((g) => (
          <div key={g.label}>
            <div className="menu-group">{g.label}</div>
            {g.rows.map((r) => (
              <div key={r.id} className={`menu-row ${r.id === state.sessionId ? 'current' : ''}`}>
                {editing === r.id ? (
                  <RenameField initial={r.title} onDone={(title) => {
                    setEditing(null);
                    if (title && title !== r.title) post({ type: 'renameSession', id: r.id, title });
                  }} />
                ) : confirming === r.id ? (
                  <div className="confirm-row" role="alertdialog" aria-label={`Delete ${r.title}?`}>
                    <span className="grow" dir="auto">Delete “{r.title}”?</span>
                    <button className="btn danger small" onClick={() => { setConfirming(null); post({ type: 'deleteSession', id: r.id }); }}>Delete</button>
                    <button className="btn ghost small" autoFocus onClick={() => setConfirming(null)}>Cancel</button>
                  </div>
                ) : (
                  <>
                    <button className="menu-item grow" role="option" aria-selected={r.id === state.sessionId}
                      onClick={() => { post({ type: 'openSession', id: r.id }); onClose(); }}>
                      <span className="ellipsis" dir="auto">{r.title}</span>
                    </button>
                    <button className="icon-btn small reveal" title="Rename" aria-label={`Rename ${r.title}`} onClick={() => setEditing(r.id)}><Icon name="edit" /></button>
                    <button className="icon-btn small reveal" title="Delete" aria-label={`Delete ${r.title}`} onClick={() => setConfirming(r.id)}><Icon name="trash" /></button>
                  </>
                )}
              </div>
            ))}
          </div>
        ))}
      </div>
      <button className="menu-footer" onClick={onNew}><Icon name="add" /> New chat</button>
    </div>
  );
}

function RenameField({ initial, onDone }: { initial: string; onDone: (title: string | null) => void }) {
  const [value, setValue] = useState(initial);
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => { ref.current?.select(); }, []);
  return (
    <input ref={ref} className="rename-input" value={value} dir="auto" aria-label="New session title"
      onChange={(e) => setValue(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') onDone(value.trim() || null);
        if (e.key === 'Escape') { e.stopPropagation(); onDone(null); }
      }}
      onBlur={() => onDone(value.trim() || null)} />
  );
}
