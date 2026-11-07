import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { MotionConfig } from 'motion/react';
import type { HostMessage } from '../../src/protocol';
import { initialState, isRunning, nextMode, reducer, type Action } from './store';
import { getPersisted, persist, post } from './vscode';
import { Header } from './components/Header';
import { Transcript } from './components/Transcript';
import { EmptyState } from './components/EmptyState';
import { Composer } from './components/Composer';
import { Approval } from './components/Approval';
import { PlanList, StatusLine } from './components/Status';
import { SettingsView } from './components/Settings';
import { MemoryView, ToolsView } from './components/Panels';
import { ErrorLine } from './components/ErrorLine';

export function App() {
  // A mode the user picked in this panel earlier (kept across reloads); read once at mount.
  const [persisted] = useState(() => getPersisted<{ mode: typeof initialState.mode }>());
  const [state, dispatch] = useReducer(reducer, initialState);
  const stateRef = useRef(state);
  stateRef.current = state;

  useEffect(() => {
    const onMessage = (e: MessageEvent<HostMessage>) => { if (e.data && typeof e.data.type === 'string') dispatch(e.data as Action); };
    window.addEventListener('message', onMessage);
    post({ type: 'ready' });
    return () => window.removeEventListener('message', onMessage);
  }, []);

  // Until init arrives the mode is only a placeholder: never persist it, or the settings default
  // (Ask, or Auto when solvra.autoApprove is on) would be overridden by it.
  const initialised = useRef(false);
  useEffect(() => {
    if (!state.ready || initialised.current) return;
    initialised.current = true;
    if (persisted.mode && persisted.mode !== state.mode) {
      dispatch({ type: 'local/setMode', mode: persisted.mode });
      post({ type: 'setMode', mode: persisted.mode });
    }
  }, [state.ready]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (initialised.current) persist({ mode: state.mode }); }, [state.mode, state.ready]);

  const running = isRunning(state);

  // esc stops a run; ⇧Tab cycles the mode (also from the input box).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isRunning(stateRef.current) && !stateRef.current.approvals.length && !document.querySelector('[data-radix-popper-content-wrapper]')) {
        e.preventDefault();
        post({ type: 'cancel' });
      }
      if (e.key === 'Tab' && e.shiftKey && stateRef.current.view === 'chat' && !stateRef.current.approvals.length) {
        e.preventDefault();
        const mode = nextMode(stateRef.current.mode);
        dispatch({ type: 'local/setMode', mode });
        post({ type: 'setMode', mode });
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const go = useCallback((view: typeof state.view) => dispatch({ type: 'local/view', view }), []);
  // Fix buttons inside the transcript ask for a view this way.
  useEffect(() => {
    const onView = (e: Event) => go((e as CustomEvent).detail);
    window.addEventListener('solvra:view', onView);
    return () => window.removeEventListener('solvra:view', onView);
  }, [go]);
  const current = state.turns[state.turns.length - 1];
  const approval = state.approvals[0];

  return (
    <MotionConfig reducedMotion="user" transition={{ duration: 0.17, ease: [0.2, 0, 0, 1] }}>
      <div className="app">
        <Header state={state} dispatch={dispatch} onSettings={() => go('settings')} />
        {state.view === 'settings' && <SettingsView state={state} onBack={() => go('chat')} />}
        {state.view === 'memory' && <MemoryView state={state} onBack={() => go('chat')} />}
        {state.view === 'tools' && <ToolsView state={state} onBack={() => go('chat')} />}
        {state.view === 'chat' && (
          <>
            <main className="scroll" aria-live="polite">
              {state.turns.length === 0
                ? <EmptyState onPick={(text) => dispatch({ type: 'prefill', text, send: false })} workspaceName={state.workspaceName} />
                : <Transcript state={state} dispatch={dispatch} />}
              {state.hostError && (
                <div className="turn"><ErrorLine message={state.hostError.message} fix={state.hostError.fix ?? null} provider={state.hostError.provider} onDismiss={() => dispatch({ type: 'local/dismissHostError' })} onOpenSettings={() => go('settings')} /></div>
              )}
            </main>
            <footer className="dock">
              {state.plan.length > 0 && (running || state.plan.some((i) => i.status !== 'done')) && <PlanList items={state.plan} />}
              {running && <StatusLine turn={current} usage={state.usage} waiting={!!approval} />}
              {approval
                ? <Approval key={approval.id} request={approval} pending={state.approvals.length} dispatch={dispatch} />
                : <Composer state={state} dispatch={dispatch} onView={go} />}
            </footer>
          </>
        )}
      </div>
    </MotionConfig>
  );
}
