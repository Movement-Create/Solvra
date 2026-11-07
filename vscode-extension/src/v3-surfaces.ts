// Where the v3 chat lives: the sidebar view and editor tabs. Also the command handlers used when
// v3 is active, so keyboard shortcuts and editor right-click actions land in the chat panel.
import * as vscode from 'vscode';
import { getV3Html } from './chat-html';
import { hub, V3Controller, V3Services } from './v3-controller';
import type { Chip } from './protocol';

export const isV3 = () => vscode.workspace.getConfiguration('solvra').get<string>('ui.version', 'v3') === 'v3';

let sidebar: V3Controller | undefined;
let lastActive: V3Controller | undefined;
const tabs = new Set<{ panel: vscode.WebviewPanel; controller: V3Controller }>();

function webviewOptions(extensionUri: vscode.Uri): vscode.WebviewOptions {
  return { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media', 'v3')] };
}

export class V3ViewProvider implements vscode.WebviewViewProvider {
  constructor(private readonly s: V3Services) {}

  resolveWebviewView(view: vscode.WebviewView): void {
    view.webview.options = webviewOptions(this.s.context.extensionUri);
    view.webview.html = getV3Html(view.webview, this.s.context.extensionUri, false);
    sidebar?.dispose();
    const controller = new V3Controller(this.s, view.webview, false);
    sidebar = controller;
    lastActive ??= controller;
    view.onDidChangeVisibility(() => { if (view.visible) lastActive = controller; });
    view.onDidDispose(() => { controller.dispose(); if (sidebar === controller) sidebar = undefined; if (lastActive === controller) lastActive = undefined; });
  }
}

export function openV3Tab(s: V3Services, sessionId: string | null): void {
  for (const t of tabs) {
    if (sessionId && t.controller.currentSessionId === sessionId) { t.panel.reveal(); return; }
  }
  const panel = vscode.window.createWebviewPanel('solvra.chatPanel', 'Solvra', vscode.ViewColumn.Active,
    { ...webviewOptions(s.context.extensionUri), retainContextWhenHidden: true });
  panel.iconPath = vscode.Uri.joinPath(s.context.extensionUri, 'resources', 'solvra-orbit.svg');
  panel.webview.html = getV3Html(panel.webview, s.context.extensionUri, true);
  const controller = new V3Controller(s, panel.webview, true, sessionId, (title) => { panel.title = title || 'Solvra'; });
  const entry = { panel, controller };
  tabs.add(entry);
  lastActive = controller;
  panel.onDidChangeViewState(() => { if (panel.active) lastActive = controller; });
  panel.onDidDispose(() => { controller.dispose(); tabs.delete(entry); if (lastActive === controller) lastActive = sidebar; });
}

/** The chat the user is looking at (a focused tab, else the sidebar), revealing the sidebar if needed. */
async function target(): Promise<V3Controller | undefined> {
  for (const t of tabs) if (t.panel.active) return t.controller;
  if (!sidebar) {
    await vscode.commands.executeCommand('solvra.chatView.focus');
    for (let i = 0; i < 20 && !sidebar; i++) await new Promise((r) => setTimeout(r, 50));
  } else {
    await vscode.commands.executeCommand('solvra.chatView.focus');
  }
  return sidebar ?? lastActive;
}

export function selectionChip(editor: vscode.TextEditor | undefined): Chip | undefined {
  if (!editor) return undefined;
  const sel = editor.selection;
  const rel = vscode.workspace.asRelativePath(editor.document.uri, false);
  const name = rel.split(/[\\/]/).pop() ?? rel;
  if (sel.isEmpty) return { kind: 'file', path: rel, label: name };
  const startLine = sel.start.line + 1;
  const endLine = sel.end.character === 0 && sel.end.line > sel.start.line ? sel.end.line : sel.end.line + 1;
  return {
    kind: 'selection', path: rel, startLine, endLine, text: editor.document.getText(sel),
    label: startLine === endLine ? `${name}:${startLine}` : `${name}:${startLine}-${endLine}`,
  };
}

/** Command overrides used when v3 is active. Each opens UI inside the chat instead of a picker. */
export function v3CommandOverrides(s: V3Services): Record<string, (...args: unknown[]) => unknown> {
  const withChat = (fn: (c: V3Controller) => void) => async () => { const c = await target(); if (c) fn(c); };
  const ask = (text: string, send: boolean) => async () => {
    const chip = selectionChip(vscode.window.activeTextEditor);
    const c = await target();
    if (!c) return;
    if (chip) c.addChip(chip);
    c.prefill(text, send && !!chip);
  };
  return {
    'solvra.openChat': withChat((c) => c.focusInput()),
    'solvra.focusInput': withChat((c) => c.focusInput()),
    'solvra.runPrompt': withChat((c) => c.focusInput()),
    'solvra.newSession': withChat((c) => c.newChat()),
    'solvra.clearSession': withChat((c) => c.newChat()),
    'solvra.pickModel': withChat((c) => c.open('model')),
    'solvra.listTools': withChat((c) => c.open('tools')),
    'solvra.listSessions': withChat((c) => c.open('sessions')),
    'solvra.addMemory': withChat((c) => c.open('memory')),
    'solvra.searchMemory': withChat((c) => c.open('memory')),
    'solvra.openSettings': withChat((c) => c.open('settings')),
    'solvra.explainSelection': ask('Explain this code.', true),
    'solvra.explainInChat': ask('Explain this code.', true),
    'solvra.fixSelection': ask('Find and fix the problems in this code.', true),
    'solvra.fixInChat': ask('Find and fix the problems in this code.', true),
    'solvra.runOnSelection': ask('', false),
    'solvra.askSolvra': ask('', false),
    'solvra.openChatInTab': (raw: unknown) => {
      const sessionId = (raw as { sessionId?: string | null } | undefined)?.sessionId ?? null;
      openV3Tab(s, sessionId);
    },
    'solvra.openSession': (raw: unknown) => {
      const id = typeof raw === 'string' ? raw : (raw as { session?: { id?: string } } | undefined)?.session?.id;
      if (id) openV3Tab(s, id);
    },
  };
}

/** Offer the editor's current selection to every v3 chat as an automatic chip. */
export function registerSelectionFeed(context: vscode.ExtensionContext): void {
  let timer: NodeJS.Timeout | undefined;
  const push = () => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      const editor = vscode.window.activeTextEditor;
      const chip = editor && !editor.selection.isEmpty && editor.document.uri.scheme === 'file' ? selectionChip(editor) ?? null : null;
      hub.broadcast(() => ({ type: 'selection', chip }));
    }, 150);
  };
  context.subscriptions.push(
    vscode.window.onDidChangeTextEditorSelection(push),
    vscode.window.onDidChangeActiveTextEditor(push),
  );
}
