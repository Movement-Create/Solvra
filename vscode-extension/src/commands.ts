import * as vscode from 'vscode';
import { AgentRunner } from './agent-runner';
import { SolvraChatProvider } from './chat-provider';
import { SolvraStatusBar } from './status-bar';
import { SessionsProvider, SessionTreeItem } from './sessions-provider';
import { SessionPanelManager } from './session-panel-manager';

export function registerCommands(
  context: vscode.ExtensionContext,
  runner: AgentRunner,
  chatProvider: SolvraChatProvider,
  statusBar: SolvraStatusBar,
  outputChannel: vscode.OutputChannel,
  sessionsProvider: SessionsProvider,
  sessionPanelManager: SessionPanelManager
): void {
  const register = (id: string, handler: (...args: unknown[]) => unknown) => {
    context.subscriptions.push(vscode.commands.registerCommand(id, handler));
  };

  // ── Chat ────────────────────────────────────────────────────────────
  register('solvra.openChat', () => {
    vscode.commands.executeCommand('solvra.chatView.focus');
  });

  register('solvra.focusInput', () => {
    chatProvider.focusInput();
  });

  register('solvra.toggleThinking', () => {
    chatProvider.toggleThinking();
  });

  register('solvra.clearSession', () => {
    chatProvider.clearSession();
  });

  register('solvra.newSession', () => {
    chatProvider.newSession();
  });

  register('solvra.openChatInTab', (rawOpts: unknown) => {
    const opts = rawOpts as { sessionId?: string | null; history?: { role: 'user' | 'assistant'; text: string }[] } | undefined;
    if (opts?.sessionId) {
      sessionPanelManager.openSession(opts.sessionId);
    } else if (opts?.history && opts.history.length > 0) {
      sessionPanelManager.createSessionWithHistory(opts.history);
    } else {
      sessionPanelManager.createSession();
    }
  });

  // ── Run prompt ──────────────────────────────────────────────────────
  register('solvra.runPrompt', async () => {
    const prompt = await vscode.window.showInputBox({
      title: 'Solvra: Run Prompt',
      prompt: 'Enter a prompt for the agent',
      placeHolder: 'e.g. Refactor the auth module to use JWT',
    });
    if (!prompt) return;

    statusBar.setRunning('Running');
    outputChannel.show(true);
    try {
      const result = await runner.run({
        prompt,
        onChunk: (chunk) => outputChannel.append(chunk),
      });
      outputChannel.appendLine(`\n--- Result ---\n${result.text}`);
      vscode.window.showInformationMessage(
        `Solvra completed (${result.turns} turns, $${result.cost_usd.toFixed(4)})`
      );
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    } finally {
      statusBar.setIdle();
    }
  });

  // ── Selection commands ──────────────────────────────────────────────
  register('solvra.runOnSelection', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const selection = editor.document.getText(editor.selection);
    if (!selection) {
      vscode.window.showWarningMessage('No text selected.');
      return;
    }
    const instruction = await vscode.window.showInputBox({
      title: 'Solvra: Run on Selection',
      prompt: 'What should Solvra do with this code?',
      placeHolder: 'e.g. Optimize this function',
    });
    if (!instruction) return;

    const prompt = `${instruction}\n\nCode:\n\`\`\`\n${selection}\n\`\`\``;
    statusBar.setRunning('Running');
    try {
      const result = await runner.run({ prompt });
      outputChannel.appendLine(`\n--- Run on Selection ---\n${result.text}`);
      outputChannel.show(true);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    } finally {
      statusBar.setIdle();
    }
  });

  register('solvra.explainSelection', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const selection = editor.document.getText(editor.selection);
    if (!selection) {
      vscode.window.showWarningMessage('No text selected.');
      return;
    }
    const prompt = `Explain the following code clearly and concisely:\n\n\`\`\`\n${selection}\n\`\`\``;
    statusBar.setRunning('Explaining');
    try {
      const result = await runner.run({ prompt });
      outputChannel.appendLine(`\n--- Explain Selection ---\n${result.text}`);
      outputChannel.show(true);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    } finally {
      statusBar.setIdle();
    }
  });

  register('solvra.fixSelection', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const selection = editor.document.getText(editor.selection);
    if (!selection) {
      vscode.window.showWarningMessage('No text selected.');
      return;
    }
    const prompt = `Fix any bugs or issues in the following code. Explain what was wrong and provide the corrected version:\n\n\`\`\`\n${selection}\n\`\`\``;
    statusBar.setRunning('Fixing');
    try {
      const result = await runner.run({ prompt });
      outputChannel.appendLine(`\n--- Fix Selection ---\n${result.text}`);
      outputChannel.show(true);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    } finally {
      statusBar.setIdle();
    }
  });

  // ── Chat context-menu commands (editor right-click) ─────────────────
  register('solvra.explainInChat', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const selection = editor.document.getText(editor.selection);
    if (!selection) {
      vscode.window.showWarningMessage('No text selected.');
      return;
    }
    const lang = editor.document.languageId;
    const prompt = `Explain this ${lang} code:\n\n\`\`\`${lang}\n${selection}\n\`\`\``;
    await chatProvider.sendPrompt(prompt);
  });

  register('solvra.fixInChat', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const selection = editor.document.getText(editor.selection);
    if (!selection) {
      vscode.window.showWarningMessage('No text selected.');
      return;
    }
    const lang = editor.document.languageId;
    const prompt = `Fix bugs in this ${lang} code and explain the changes:\n\n\`\`\`${lang}\n${selection}\n\`\`\``;
    await chatProvider.sendPrompt(prompt);
  });

  register('solvra.askSolvra', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const selection = editor.document.getText(editor.selection);
    if (!selection) {
      vscode.window.showWarningMessage('No text selected.');
      return;
    }
    const question = await vscode.window.showInputBox({
      title: 'Solvra: Ask about selection',
      prompt: 'What would you like to know about this code?',
      placeHolder: 'e.g. How can I optimize this?',
    });
    if (!question) return;

    const lang = editor.document.languageId;
    const prompt = `${question}\n\n\`\`\`${lang}\n${selection}\n\`\`\``;
    await chatProvider.sendPrompt(prompt);
  });

  // ── Tools and memory ────────────────────────────────────────────────
  register('solvra.listTools', async () => {
    statusBar.setRunning('Loading tools');
    try {
      const tools = await runner.listTools();
      outputChannel.appendLine('\n--- Available Tools ---');
      for (const tool of tools) {
        outputChannel.appendLine(`  ${tool}`);
      }
      outputChannel.show(true);
      vscode.window.showInformationMessage(`Solvra: ${tools.length} tools available. See Output panel.`);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    } finally {
      statusBar.setIdle();
    }
  });

  register('solvra.listSessions', async () => {
    statusBar.setRunning('Loading sessions');
    try {
      const sessions = await runner.listSessions();
      outputChannel.appendLine('\n--- Sessions ---');
      for (const s of sessions) {
        outputChannel.appendLine(`  ${s}`);
      }
      outputChannel.show(true);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    } finally {
      statusBar.setIdle();
    }
  });

  register('solvra.addMemory', async () => {
    const fact = await vscode.window.showInputBox({
      title: 'Solvra: Add Memory',
      prompt: 'Enter a fact to remember',
      placeHolder: 'e.g. The auth service uses JWT tokens stored in Redis',
    });
    if (!fact) return;

    try {
      const result = await runner.addMemory(fact);
      vscode.window.showInformationMessage(`Solvra memory added: ${result || 'OK'}`);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    }
  });

  register('solvra.searchMemory', async () => {
    const query = await vscode.window.showInputBox({
      title: 'Solvra: Search Memory',
      prompt: 'Search for memories',
      placeHolder: 'e.g. auth',
    });
    if (!query) return;

    try {
      const results = await runner.searchMemory(query);
      if (results.length === 0) {
        vscode.window.showInformationMessage('Solvra: No memories found.');
      } else {
        outputChannel.appendLine('\n--- Memory Search ---');
        for (const r of results) {
          outputChannel.appendLine(`  ${r}`);
        }
        outputChannel.show(true);
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showErrorMessage(`Solvra error: ${msg}`);
    }
  });

  // ── Sessions tree ───────────────────────────────────────────────────
  register('solvra.refreshSessions', () => {
    sessionsProvider.refresh();
  });

  register('solvra.openSession', (idOrItem: unknown) => {
    let sessionId: string | undefined;
    if (typeof idOrItem === 'string') {
      sessionId = idOrItem;
    } else if (idOrItem instanceof SessionTreeItem) {
      sessionId = idOrItem.meta.id;
    }
    if (!sessionId) return;
    chatProvider.openSession(sessionId);
  });

  register('solvra.renameSession', async (item: unknown) => {
    let sessionId: string | undefined;
    let currentTitle = '';
    if (item instanceof SessionTreeItem) {
      sessionId = item.meta.id;
      currentTitle = item.meta.title;
    }
    if (!sessionId) return;

    const newTitle = await vscode.window.showInputBox({
      title: 'Rename Session',
      value: currentTitle,
      prompt: 'Enter a new name for this session',
    });
    if (newTitle === undefined || newTitle === currentTitle) return;

    const ok = sessionsProvider.renameSession(sessionId, newTitle);
    if (!ok) {
      vscode.window.showErrorMessage('Failed to rename session.');
    }
  });

  register('solvra.deleteSession', async (item: unknown) => {
    let sessionId: string | undefined;
    let title = 'this session';
    if (item instanceof SessionTreeItem) {
      sessionId = item.meta.id;
      title = item.meta.title;
    }
    if (!sessionId) return;

    const confirm = await vscode.window.showWarningMessage(
      `Delete session "${title}"?`,
      { modal: true },
      'Delete'
    );
    if (confirm !== 'Delete') return;

    const ok = sessionsProvider.deleteSession(sessionId);
    if (!ok) {
      vscode.window.showErrorMessage('Failed to delete session.');
    }
  });

  register('solvra.toggleShowAllSessions', () => {
    sessionsProvider.toggleShowAll();
  });
}
