import * as vscode from 'vscode';
import { AgentRunner } from './agent-runner';
import { SolvraStatusBar } from './status-bar';
import { SessionsProvider } from './sessions-provider';
import { getChatHtml, UiVersion } from './chat-html';

export class SolvraChatProvider implements vscode.WebviewViewProvider {
  public static readonly viewType = 'solvra.chatView';

  private _view?: vscode.WebviewView;
  private _sessionsProvider?: SessionsProvider;
  private _currentSessionId: string | null = null;
  private _isRunning = false;
  private _showThinking = false;
  private _history: { role: 'user' | 'assistant'; text: string }[] = [];

  constructor(
    private readonly _extensionUri: vscode.Uri,
    private readonly _runner: AgentRunner,
    private readonly _statusBar: SolvraStatusBar
  ) {}

  setSessionsProvider(provider: SessionsProvider): void {
    this._sessionsProvider = provider;
  }

  resolveWebviewView(
    webviewView: vscode.WebviewView,
    _context: vscode.WebviewViewResolveContext,
    _token: vscode.CancellationToken
  ): void {
    this._view = webviewView;

    webviewView.webview.options = {
      enableScripts: true,
      localResourceRoots: [this._extensionUri],
    };

    webviewView.webview.html = this._getHtml(webviewView.webview);

    webviewView.webview.onDidReceiveMessage(async (message) => {
      switch (message.type) {
        case 'sendMessage':
          await this._handleUserMessage(message.text);
          break;
        case 'cancel':
          this._runner.cancel();
          this._isRunning = false;
          this._statusBar.setIdle();
          this._postMessage({ type: 'runComplete' });
          break;
        case 'newSession':
          this.newSession();
          break;
        case 'clearSession':
          this.clearSession();
          break;
        case 'searchFiles':
          await this._handleFileSearch(message.query);
          break;
        case 'readFile':
          await this._handleReadFile(message.path);
          break;
        case 'slashCommand':
          await this._handleSlashCommand(message.command, message.args);
          break;
        case 'attachFile':
          this.attachFileContent(message.path, message.relative, message.content);
          break;
        case 'openInTab':
          vscode.commands.executeCommand('solvra.openChatInTab', {
            sessionId: this._currentSessionId,
            history: this._history,
          });
          break;
      }
    });

    const cfgSub = vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('solvra.ui.version') && this._view) {
        this._view.webview.html = this._getHtml(this._view.webview);
      }
    });
    webviewView.onDidDispose(() => cfgSub.dispose());
  }

  /** Send a prefilled prompt to the chat (used by explainInChat, fixInChat, askSolvra). */
  async sendPrompt(text: string): Promise<void> {
    this._focusView();
    // Post to webview so it appears in the message list
    this._postMessage({ type: 'addUserMessage', text });
    await this._handleUserMessage(text);
  }

  focusInput(): void {
    this._focusView();
    this._postMessage({ type: 'focusInput' });
  }

  toggleThinking(): void {
    this._showThinking = !this._showThinking;
    this._postMessage({ type: 'toggleThinking', show: this._showThinking });
  }

  clearSession(): void {
    this._currentSessionId = null;
    this._history = [];
    this._postMessage({ type: 'clearMessages' });
    if (this._sessionsProvider) {
      this._sessionsProvider.setActive(null);
    }
  }

  newSession(): void {
    this.clearSession();
  }

  openSession(sessionId: string): void {
    this._currentSessionId = sessionId;
    if (this._sessionsProvider) {
      this._sessionsProvider.setActive(sessionId);
      const messages = this._sessionsProvider.replaySession(sessionId);
      this._postMessage({ type: 'clearMessages' });
      for (const msg of messages) {
        this._postMessage({
          type: msg.role === 'user' ? 'addUserMessage' : 'addAssistantMessage',
          text: msg.content,
        });
      }
    }
    this._focusView();
  }

  getHistory(): { role: 'user' | 'assistant'; text: string }[] {
    return this._history;
  }

  getCurrentSessionId(): string | null {
    return this._currentSessionId;
  }

  // --- Slash commands ---
  private static readonly SLASH_COMMANDS: { name: string; description: string; args?: string }[] = [
    { name: 'model', description: 'Switch model', args: '<model-name>' },
    { name: 'provider', description: 'Switch provider', args: '<anthropic|google|openai|ollama|moonshot>' },
    { name: 'clear', description: 'Clear chat and start new session' },
    { name: 'help', description: 'Show available commands' },
    { name: 'effort', description: 'Set effort level', args: '<low|medium|high|max>' },
    { name: 'tools', description: 'List available tools' },
    { name: 'sessions', description: 'List past sessions' },
    { name: 'memory', description: 'Search memory', args: '<query>' },
    { name: 'compact', description: 'Compact conversation history' },
  ];

  private async _handleSlashCommand(command: string, args: string): Promise<void> {
    switch (command) {
      case 'model': {
        if (args.trim()) {
          this._runner.setConfig({ model: args.trim() });
          const provider = this._detectProviderForModel(args.trim());
          if (provider) { this._runner.setConfig({ provider }); }
          this._postMessage({ type: 'addAssistantMessage', text: `Switched to model: **${args.trim()}**${provider ? ` (provider: ${provider})` : ''}` });
          this._statusBar.setIdle();
          break;
        }
        const config = vscode.workspace.getConfiguration('solvra');
        const current = this._runner.getRunnerConfig().model || config.get<string>('model', 'gemini-2.5-flash');
        const models = [
          { label: 'gemini-2.5-flash', description: 'Google — fast & cheap', provider: 'google' },
          { label: 'gemini-2.5-pro', description: 'Google — smartest Gemini', provider: 'google' },
          { label: 'claude-sonnet-4-20250514', description: 'Anthropic — balanced', provider: 'anthropic' },
          { label: 'claude-3-5-sonnet-20241022', description: 'Anthropic — fast', provider: 'anthropic' },
          { label: 'claude-opus-4-20250514', description: 'Anthropic — most capable', provider: 'anthropic' },
          { label: 'gpt-4o', description: 'OpenAI — flagship', provider: 'openai' },
          { label: 'gpt-4o-mini', description: 'OpenAI — fast & cheap', provider: 'openai' },
          { label: 'o3-mini', description: 'OpenAI — reasoning', provider: 'openai' },
          { label: 'kimi-k2.5', description: 'Moonshot — Kimi K2.5 (vision + reasoning)', provider: 'moonshot' },
          { label: 'kimi-k2-thinking', description: 'Moonshot — Kimi K2 with reasoning', provider: 'moonshot' },
          { label: 'kimi-k2-turbo-preview', description: 'Moonshot — Kimi K2 Turbo (fast)', provider: 'moonshot' },
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
          this._postMessage({ type: 'addAssistantMessage', text: `Switched to model: **${picked.modelId}** (provider: ${picked.provider})` });
          this._statusBar.setIdle();
        }
        break;
      }
      case 'provider': {
        if (args.trim()) {
          this._runner.setConfig({ provider: args.trim() });
          this._postMessage({ type: 'addAssistantMessage', text: `Switched to provider: **${args.trim()}**` });
          break;
        }
        const provConfig = vscode.workspace.getConfiguration('solvra');
        const curProvider = this._runner.getRunnerConfig().provider || provConfig.get<string>('provider', 'google');
        const providers = [
          { label: 'google', description: 'Gemini models' },
          { label: 'anthropic', description: 'Claude models' },
          { label: 'openai', description: 'GPT / o-series models' },
          { label: 'ollama', description: 'Local models via Ollama' },
          { label: 'moonshot', description: 'Moonshot AI models' },
        ];
        const provItems = providers.map(p => ({
          label: p.label === curProvider ? `$(check) ${p.label}` : p.label,
          description: p.description,
          providerId: p.label,
        }));
        const pickedProv = await vscode.window.showQuickPick(provItems, {
          placeHolder: `Current: ${curProvider} — select a provider`,
          title: 'Switch Provider',
        });
        if (pickedProv) {
          this._runner.setConfig({ provider: pickedProv.providerId });
          this._postMessage({ type: 'addAssistantMessage', text: `Switched to provider: **${pickedProv.providerId}**` });
        }
        break;
      }
      case 'clear':
        this.clearSession();
        this._postMessage({ type: 'addAssistantMessage', text: 'Session cleared.' });
        break;
      case 'effort': {
        if (args.trim()) {
          this._runner.setConfig({ effort: args.trim() });
          this._postMessage({ type: 'addAssistantMessage', text: `Effort set to: **${args.trim()}**` });
          break;
        }
        const effortItems = [
          { label: 'low', description: 'Fastest, least thorough' },
          { label: 'medium', description: 'Balanced speed & quality' },
          { label: 'high', description: 'Thorough, slower' },
          { label: 'max', description: 'Most thorough, slowest' },
        ];
        const pickedEffort = await vscode.window.showQuickPick(effortItems, {
          placeHolder: 'Select effort level',
          title: 'Set Effort',
        });
        if (pickedEffort) {
          this._runner.setConfig({ effort: pickedEffort.label });
          this._postMessage({ type: 'addAssistantMessage', text: `Effort set to: **${pickedEffort.label}**` });
        }
        break;
      }
      case 'tools': {
        try {
          const tools = await this._runner.listTools();
          this._postMessage({ type: 'addAssistantMessage', text: '**Available tools:**\n' + tools.map(t => `- ${t}`).join('\n') });
        } catch (e: unknown) {
          this._postMessage({ type: 'addErrorMessage', text: `Failed to list tools: ${e instanceof Error ? e.message : e}` });
        }
        break;
      }
      case 'sessions': {
        try {
          const sessions = await this._runner.listSessions();
          this._postMessage({ type: 'addAssistantMessage', text: '**Sessions:**\n' + (sessions.length ? sessions.map(s => `- ${s}`).join('\n') : '(none)') });
        } catch (e: unknown) {
          this._postMessage({ type: 'addErrorMessage', text: `Failed to list sessions: ${e instanceof Error ? e.message : e}` });
        }
        break;
      }
      case 'memory': {
        if (!args.trim()) {
          this._postMessage({ type: 'addAssistantMessage', text: 'Usage: /memory <search query>' });
          return;
        }
        try {
          const results = await this._runner.searchMemory(args.trim());
          this._postMessage({ type: 'addAssistantMessage', text: '**Memory results:**\n' + (results.length ? results.map(r => `- ${r}`).join('\n') : '(nothing found)') });
        } catch (e: unknown) {
          this._postMessage({ type: 'addErrorMessage', text: `Failed to search memory: ${e instanceof Error ? e.message : e}` });
        }
        break;
      }
      case 'compact': {
        if (this._history.length > 8) {
          this._history = this._history.slice(-8);
          this._postMessage({ type: 'addAssistantMessage', text: 'Conversation history compacted (keeping last 4 exchanges).' });
        } else {
          this._postMessage({ type: 'addAssistantMessage', text: 'History is already short, nothing to compact.' });
        }
        break;
      }
      case 'help':
      default:
        this._postMessage({
          type: 'addAssistantMessage',
          text: '**Slash commands:**\n' +
            SolvraChatProvider.SLASH_COMMANDS.map(c =>
              `- **/${c.name}**${c.args ? ' ' + c.args : ''} — ${c.description}`
            ).join('\n') +
            '\n\n**File references:**\n- Type **@** followed by a filename to attach file contents to your message'
        });
        break;
    }
  }

  private _detectProviderForModel(model: string): string | null {
    if (/^claude-/i.test(model)) return 'anthropic';
    if (/^gpt-|^o1/i.test(model)) return 'openai';
    if (/^gemini-/i.test(model)) return 'google';
    if (/^(llama|mistral|codellama|phi|qwen)/i.test(model)) return 'ollama';
    if (/^(moonshot-|kimi)/i.test(model)) return 'moonshot';
    return null;
  }

  // --- @ file references ---
  private async _handleFileSearch(query: string): Promise<void> {
    if (!query || query.length < 1) {
      this._postMessage({ type: 'fileResults', files: [] });
      return;
    }
    try {
      const files = await vscode.workspace.findFiles(
        `**/*${query}*`,
        '**/node_modules/**',
        15
      );
      const results = files.map(f => {
        const rel = vscode.workspace.asRelativePath(f);
        return { path: f.fsPath, relative: rel };
      });
      this._postMessage({ type: 'fileResults', files: results });
    } catch {
      this._postMessage({ type: 'fileResults', files: [] });
    }
  }

  private async _handleReadFile(filePath: string): Promise<void> {
    try {
      const uri = vscode.Uri.file(filePath);
      const doc = await vscode.workspace.openTextDocument(uri);
      const content = doc.getText();
      const rel = vscode.workspace.asRelativePath(uri);
      const maxChars = 10000;
      const truncated = content.length > maxChars
        ? content.slice(0, maxChars) + `\n...[truncated, ${content.length} chars total]`
        : content;
      this._postMessage({ type: 'fileContent', path: filePath, relative: rel, content: truncated });
    } catch (e: unknown) {
      this._postMessage({ type: 'fileContent', path: filePath, relative: filePath, content: `(error reading file: ${e instanceof Error ? e.message : e})` });
    }
  }

  private _focusView(): void {
    if (this._view) {
      this._view.show?.(true);
    }
  }

  private _postMessage(message: unknown): void {
    this._view?.webview.postMessage(message);
  }

  private _pendingFileAttachments: Map<string, { relative: string; content: string }> = new Map();

  attachFileContent(path: string, relative: string, content: string): void {
    this._pendingFileAttachments.set(path, { relative, content });
  }

  private _buildPromptWithHistory(text: string): string {
    let fileContext = '';
    if (this._pendingFileAttachments.size > 0) {
      const parts: string[] = [];
      for (const [, { relative, content }] of this._pendingFileAttachments) {
        parts.push(`<file path="${relative}">\n${content}\n</file>`);
      }
      fileContext = '<attached_files>\n' + parts.join('\n') + '\n</attached_files>\n\n';
      this._pendingFileAttachments.clear();
    }

    if (this._history.length === 0) return fileContext + text;

    const historyLines = this._history.map(
      (msg) => `${msg.role === 'user' ? 'User' : 'Assistant'}: ${msg.text}`
    );

    return (
      '<conversation_history>\n' +
      historyLines.join('\n\n') +
      '\n</conversation_history>\n\n' +
      fileContext +
      'Continue the conversation above. The user\'s latest message is:\n\n' +
      text
    );
  }

  private async _handleUserMessage(text: string): Promise<void> {
    if (this._isRunning || !text.trim()) return;

    const slashMatch = text.trim().match(/^\/([a-zA-Z]+)(?:\s+(.*))?$/);
    if (slashMatch) {
      await this._handleSlashCommand(slashMatch[1], slashMatch[2] || '');
      return;
    }

    this._isRunning = true;
    this._statusBar.setRunning('Thinking');
    this._postMessage({ type: 'runStarted' });

    this._history.push({ role: 'user', text });

    try {
      let streamed = '';
      const promptWithHistory = this._buildPromptWithHistory(text);
      const result = await this._runner.run({
        prompt: promptWithHistory,
        sessionId: this._currentSessionId ?? undefined,
        onChunk: (chunk: string) => {
          streamed += chunk;
          this._postMessage({ type: 'streamChunk', text: chunk });
        },
      });

      const assistantText = streamed.trim() || result.text || '';
      if (assistantText) {
        this._history.push({ role: 'assistant', text: assistantText });
      }

      if (!streamed.trim() && result.text) {
        this._postMessage({ type: 'addAssistantMessage', text: result.text });
      } else {
        this._postMessage({ type: 'streamEnd' });
      }

      this._statusBar.setIdle();

      if (this._sessionsProvider) {
        this._sessionsProvider.refresh();
      }
    } catch (err: unknown) {
      const errorMsg = err instanceof Error ? err.message : String(err);
      this._postMessage({ type: 'addErrorMessage', text: errorMsg });
      this._statusBar.setError('Failed');
    } finally {
      this._isRunning = false;
      this._postMessage({ type: 'runComplete' });
    }
  }

  private _getHtml(webview: vscode.Webview): string {
    const version = vscode.workspace
      .getConfiguration('solvra')
      .get<UiVersion>('ui.version', 'legacy');
    return getChatHtml(webview, this._extensionUri, version);
  }
}
