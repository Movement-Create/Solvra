import * as vscode from 'vscode';
import { AgentRunner } from './agent-runner';
import { SolvraStatusBar } from './status-bar';
import { SessionsProvider } from './sessions-provider';

/**
 * Manages editor-tab-based session panels (as opposed to the sidebar chat).
 * Each session gets its own WebviewPanel that can be docked alongside editors.
 */
export class SessionPanelManager {
  private _panels = new Map<string, vscode.WebviewPanel>();

  constructor(
    private readonly _extensionUri: vscode.Uri,
    private readonly _runner: AgentRunner,
    private readonly _statusBar: SolvraStatusBar,
    private readonly _sessionsProvider: SessionsProvider
  ) {}

  /**
   * Open (or focus) a session in an editor tab panel.
   */
  openSession(sessionId: string): void {
    const existing = this._panels.get(sessionId);
    if (existing) {
      existing.reveal(vscode.ViewColumn.One);
      return;
    }

    const sessions = this._sessionsProvider.listSessionsFromDisk();
    const meta = sessions.find((s) => s.id === sessionId);
    const title = meta?.title ?? `Session ${sessionId.slice(0, 8)}`;

    const panel = this._createPanel(`Solvra: ${title}`, sessionId);

    // Replay existing messages
    const messages = this._sessionsProvider.replaySession(sessionId);
    for (const msg of messages) {
      panel.webview.postMessage({
        type: msg.role === 'user' ? 'addUserMessage' : 'addAssistantMessage',
        text: msg.content,
      });
    }
  }

  /**
   * Create a new session panel (no pre-existing session ID).
   */
  createSession(): void {
    const sessionId = generateId();
    this._createPanel('Solvra: New Session', sessionId);
  }

  /**
   * Create a new tab panel and replay existing history into it.
   */
  createSessionWithHistory(history: { role: 'user' | 'assistant'; text: string }[]): void {
    const sessionId = generateId();
    const panel = this._createPanel('Solvra: Chat', sessionId);

    // Replay messages after a short delay to ensure webview is ready
    setTimeout(() => {
      for (const msg of history) {
        panel.webview.postMessage({
          type: msg.role === 'user' ? 'addUserMessage' : 'addAssistantMessage',
          text: msg.text,
        });
      }
    }, 100);
  }

  private _createPanel(title: string, sessionId: string): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(
      'solvra.sessionPanel',
      title,
      vscode.ViewColumn.One,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [this._extensionUri],
      }
    );

    this._panels.set(sessionId, panel);

    panel.onDidDispose(() => {
      this._panels.delete(sessionId);
    });

    panel.webview.html = this._getHtml(panel.webview, title);
    this._setupPanelHandlers(panel, sessionId);

    return panel;
  }

  private _setupPanelHandlers(panel: vscode.WebviewPanel, sessionId: string): void {
    const history: { role: 'user' | 'assistant'; text: string }[] = [];
    const pendingFiles = new Map<string, { relative: string; content: string }>();

    panel.webview.onDidReceiveMessage(async (message) => {
      switch (message.type) {
        case 'sendMessage':
          await this._handlePanelMessage(panel, sessionId, message.text, history, pendingFiles);
          break;
        case 'cancel':
          this._runner.cancel();
          this._statusBar.setIdle();
          panel.webview.postMessage({ type: 'runComplete' });
          break;
        case 'newSession': {
          history.length = 0;
          pendingFiles.clear();
          panel.webview.postMessage({ type: 'clearMessages' });
          break;
        }
        case 'searchFiles':
          await this._handleFileSearch(panel, message.query);
          break;
        case 'readFile':
          await this._handleReadFile(panel, message.path);
          break;
        case 'slashCommand':
          await this._handleSlashCommand(panel, message.command, message.args);
          break;
        case 'attachFile':
          pendingFiles.set(message.path, { relative: message.relative, content: message.content });
          break;
      }
    });
  }

  private async _handleSlashCommand(panel: vscode.WebviewPanel, command: string, args: string): Promise<void> {
    switch (command) {
      case 'model': {
        if (args.trim()) {
          this._runner.setConfig({ model: args.trim() });
          panel.webview.postMessage({ type: 'addAssistantMessage', text: `Switched to model: **${args.trim()}**` });
          break;
        }
        const config = vscode.workspace.getConfiguration('solvra');
        const current = this._runner.getRunnerConfig().model || config.get<string>('model', 'gemini-2.5-flash');
        const models = [
          { label: 'gemini-2.5-flash', description: 'Google — fast & cheap', provider: 'google' },
          { label: 'gemini-2.5-pro', description: 'Google — smartest Gemini', provider: 'google' },
          { label: 'claude-sonnet-4-20250514', description: 'Anthropic — balanced', provider: 'anthropic' },
          { label: 'claude-opus-4-20250514', description: 'Anthropic — most capable', provider: 'anthropic' },
          { label: 'gpt-4o', description: 'OpenAI — flagship', provider: 'openai' },
          { label: 'gpt-4o-mini', description: 'OpenAI — fast & cheap', provider: 'openai' },
        ];
        const items = models.map(m => ({
          label: m.label === current ? `$(check) ${m.label}` : m.label,
          description: m.description,
          modelId: m.label,
          provider: m.provider,
        }));
        const picked = await vscode.window.showQuickPick(items, {
          placeHolder: `Current: ${current} — select a model`,
          title: 'Switch Model',
        });
        if (picked) {
          this._runner.setConfig({ model: picked.modelId, provider: picked.provider });
          panel.webview.postMessage({ type: 'addAssistantMessage', text: `Switched to model: **${picked.modelId}** (provider: ${picked.provider})` });
        }
        break;
      }
      case 'clear':
        panel.webview.postMessage({ type: 'clearMessages' });
        panel.webview.postMessage({ type: 'addAssistantMessage', text: 'Session cleared.' });
        break;
      case 'help':
      default:
        panel.webview.postMessage({
          type: 'addAssistantMessage',
          text: '**Slash commands:**\n- **/model** — Switch model\n- **/clear** — Clear session\n- **/help** — Show commands'
        });
        break;
    }
  }

  private async _handleFileSearch(panel: vscode.WebviewPanel, query: string): Promise<void> {
    if (!query || query.length < 1) {
      panel.webview.postMessage({ type: 'fileResults', files: [] });
      return;
    }
    try {
      const files = await vscode.workspace.findFiles(
        `**/*${query}*`,
        '**/node_modules/**',
        15
      );
      const results = files.map(f => ({
        path: f.fsPath,
        relative: vscode.workspace.asRelativePath(f),
      }));
      panel.webview.postMessage({ type: 'fileResults', files: results });
    } catch {
      panel.webview.postMessage({ type: 'fileResults', files: [] });
    }
  }

  private async _handleReadFile(panel: vscode.WebviewPanel, filePath: string): Promise<void> {
    try {
      const uri = vscode.Uri.file(filePath);
      const doc = await vscode.workspace.openTextDocument(uri);
      const content = doc.getText();
      const rel = vscode.workspace.asRelativePath(uri);
      const maxChars = 10000;
      const truncated = content.length > maxChars
        ? content.slice(0, maxChars) + `\n...[truncated, ${content.length} chars total]`
        : content;
      panel.webview.postMessage({ type: 'fileContent', path: filePath, relative: rel, content: truncated });
    } catch (e: unknown) {
      panel.webview.postMessage({ type: 'fileContent', path: filePath, relative: filePath, content: `(error reading file: ${e instanceof Error ? e.message : e})` });
    }
  }

  private async _handlePanelMessage(
    panel: vscode.WebviewPanel,
    sessionId: string,
    text: string,
    history: { role: 'user' | 'assistant'; text: string }[],
    pendingFiles: Map<string, { relative: string; content: string }>
  ): Promise<void> {
    if (!text.trim()) return;

    // Check for slash commands
    const slashMatch = text.trim().match(/^\/([a-zA-Z]+)(?:\s+(.*))?$/);
    if (slashMatch) {
      await this._handleSlashCommand(panel, slashMatch[1], slashMatch[2] || '');
      return;
    }

    this._statusBar.setRunning('Thinking');
    panel.webview.postMessage({ type: 'runStarted' });

    history.push({ role: 'user', text });

    try {
      // Build file context
      let fileContext = '';
      if (pendingFiles.size > 0) {
        const parts: string[] = [];
        for (const [, { relative, content }] of pendingFiles) {
          parts.push(`<file path="${relative}">\n${content}\n</file>`);
        }
        fileContext = '<attached_files>\n' + parts.join('\n') + '\n</attached_files>\n\n';
        pendingFiles.clear();
      }

      let streamed = '';
      const prompt = fileContext + this._buildPromptWithHistory(text, history);
      const result = await this._runner.run({
        prompt,
        sessionId,
        onChunk: (chunk: string) => {
          streamed += chunk;
          panel.webview.postMessage({ type: 'streamChunk', text: chunk });
        },
      });

      const assistantText = streamed.trim() || result.text || '';
      if (assistantText) {
        history.push({ role: 'assistant', text: assistantText });
      }

      if (!streamed.trim() && result.text) {
        panel.webview.postMessage({ type: 'addAssistantMessage', text: result.text });
      } else {
        panel.webview.postMessage({ type: 'streamEnd' });
      }

      this._statusBar.setIdle();
      this._sessionsProvider.refresh();
    } catch (err: unknown) {
      const errorMsg = err instanceof Error ? err.message : String(err);
      panel.webview.postMessage({ type: 'addErrorMessage', text: errorMsg });
      this._statusBar.setError('Failed');
    } finally {
      panel.webview.postMessage({ type: 'runComplete' });
    }
  }

  private _buildPromptWithHistory(
    text: string,
    history: { role: 'user' | 'assistant'; text: string }[]
  ): string {
    // Only include previous messages (not the current one just pushed)
    const prior = history.slice(0, -1);
    if (prior.length === 0) return text;

    const historyLines = prior.map(
      (msg) => `${msg.role === 'user' ? 'User' : 'Assistant'}: ${msg.text}`
    );

    return (
      '<conversation_history>\n' +
      historyLines.join('\n\n') +
      '\n</conversation_history>\n\n' +
      'Continue the conversation above. The user\'s latest message is:\n\n' +
      text
    );
  }

  private _getHtml(webview: vscode.Webview, title: string): string {
    const nonce = getNonce();
    const cssUri = webview.asWebviewUri(
      vscode.Uri.joinPath(this._extensionUri, 'media', 'chat.css')
    );
    const jsUri = webview.asWebviewUri(
      vscode.Uri.joinPath(this._extensionUri, 'media', 'chat.js')
    );
    const safeTitle = escapeHtml(title);

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy"
  content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${cssUri}">
<title>${safeTitle}</title>
</head>
<body>
  <div id="header">
    <div id="header-left">${safeTitle}</div>
  </div>

  <div id="messages">
    <div class="empty-state" id="empty-state">
      <div class="empty-state-icon">S</div>
      <div class="empty-state-title">What can I help you with?</div>
      <div class="empty-state-subtitle">Ask questions, write code, debug issues, or explore your codebase.</div>
      <div class="empty-state-hints">
        <span><kbd>/</kbd> for commands</span>
        <span><kbd>@</kbd> to attach files</span>
        <span><kbd>Shift+Enter</kbd> for new line</span>
      </div>
    </div>
  </div>

  <div class="progress-container" id="progress">
    <div class="progress-dots">
      <div class="progress-dot"></div>
      <div class="progress-dot"></div>
      <div class="progress-dot"></div>
    </div>
    <div class="progress-label" id="progress-label">Thinking...</div>
  </div>

  <div id="file-chips"></div>

  <div id="input-wrapper">
    <div id="autocomplete"></div>
    <div id="input-area">
      <div id="input-container">
        <textarea id="input" rows="1" placeholder="Ask Solvra... (/ for commands, @ for files)"></textarea>
        <div id="input-footer">
          <span>Shift+Enter for new line</span>
          <span id="char-count"></span>
        </div>
      </div>
      <button class="action-btn" id="send-btn" title="Send (Enter)">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M1.724 1.053a.5.5 0 01.555-.033l12 7a.5.5 0 010 .86l-12 7A.5.5 0 011.5 15.5V.5a.5.5 0 01.224-.447zM3 2.31v4.19h4.5a.5.5 0 010 1H3v4.19l9.144-4.69L3 2.31z"/></svg>
        Send
      </button>
      <button class="action-btn" id="cancel-btn" title="Stop">
        <svg viewBox="0 0 16 16" fill="currentColor"><rect x="3" y="3" width="10" height="10" rx="1"/></svg>
        Stop
      </button>
    </div>
  </div>

<script nonce="${nonce}" src="${jsUri}"></script>
</body>
</html>`;
  }
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let nonce = '';
  for (let i = 0; i < 32; i++) {
    nonce += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return nonce;
}

function generateId(): string {
  const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let id = '';
  for (let i = 0; i < 16; i++) {
    id += chars[Math.floor(Math.random() * chars.length)];
  }
  return id;
}
