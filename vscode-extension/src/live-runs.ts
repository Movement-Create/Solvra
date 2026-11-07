// Runs shared between views (the sidebar chat and editor tabs). No `vscode` import (unit-tested).
import type { StreamRun } from './stream-run';
import type { HostMessage, TranscriptMessage } from './protocol';

/** Anything that shows a run: a chat view. */
export interface Viewer {
  post(msg: HostMessage): void;
  /** The run finished. */
  runEnded(run: ActiveRun): void;
  /** A new chat's run got its session id and title. */
  adoptSession(id: string, title: string): void;
}

/**
 * A run in progress. It belongs to its session, not to the view that started it: every chat
 * showing that session (sidebar, editor tabs) is attached and sees it live, and a view that opens
 * the session later gets the turn so far replayed.
 */
export class ActiveRun {
  readonly viewers = new Set<Viewer>();
  private readonly history: HostMessage[] = [];
  run: Pick<StreamRun, 'cancel' | 'respond'> | null = null;

  constructor(readonly id: string, readonly prompt: string, private readonly started: HostMessage, public sessionId: string | null) {}

  /** Send to every attached view, and keep it for views that attach later. */
  emit(msg: HostMessage): void {
    this.history.push(msg);
    for (const v of this.viewers) v.post(msg);
  }

  attach(view: Viewer): void {
    if (this.viewers.has(view)) return;
    this.viewers.add(view);
    view.post(this.started);
    for (const m of this.history) view.post(m);
  }

  /** A view stops showing the run. The run is stopped only when nobody can see it any more. */
  detach(view: Viewer): void {
    if (!this.viewers.delete(view)) return;
    if (this.viewers.size === 0) this.run?.cancel();
  }
}


/**
 * The saved transcript of a session whose run is still going ends with that run's prompt and
 * whatever it has logged so far. Drop that part: the live run replays it.
 */
export function withoutLiveTurn(messages: TranscriptMessage[], prompt: string): TranscriptMessage[] {
  for (let i = messages.length - 1; i >= 0; i--) {
    if (messages[i].role !== 'user') continue;
    return messages[i].text.trim() === prompt.trim() ? messages.slice(0, i) : messages;
  }
  return messages;
}
