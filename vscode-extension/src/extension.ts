import * as vscode from 'vscode';
import { AgentRunner } from './agent-runner';
import { SolvraChatProvider } from './chat-provider';
import { SolvraStatusBar } from './status-bar';
import { registerCommands } from './commands';
import { SessionsProvider } from './sessions-provider';
import { SessionPanelManager } from './session-panel-manager';

export function activate(context: vscode.ExtensionContext) {
  // Create output channel
  const outputChannel = vscode.window.createOutputChannel('Solvra');
  context.subscriptions.push(outputChannel);

  outputChannel.appendLine('Solvra extension activating...');

  // Create core services
  const statusBar = new SolvraStatusBar();
  context.subscriptions.push({ dispose: () => statusBar.dispose() });

  const runner = new AgentRunner(outputChannel);

  // Create and register the chat webview provider
  const chatProvider = new SolvraChatProvider(
    context.extensionUri,
    runner,
    statusBar
  );

  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider(
      SolvraChatProvider.viewType,
      chatProvider,
      {
        webviewOptions: {
          retainContextWhenHidden: true,
        },
      }
    )
  );

  // Sessions tree view
  const sessionsProvider = new SessionsProvider(runner);
  chatProvider.setSessionsProvider(sessionsProvider);
  context.subscriptions.push(
    vscode.window.registerTreeDataProvider(
      SessionsProvider.viewType,
      sessionsProvider
    )
  );

  // Session panel manager (tab-based sessions)
  const sessionPanelManager = new SessionPanelManager(
    context.extensionUri,
    runner,
    statusBar,
    sessionsProvider
  );

  // Register all commands
  registerCommands(
    context,
    runner,
    chatProvider,
    statusBar,
    outputChannel,
    sessionsProvider,
    sessionPanelManager
  );

  // First-run API key check
  const solvraConfig = vscode.workspace.getConfiguration('solvra');
  const hasSettingsKey =
    solvraConfig.get<string>('googleApiKey', '') ||
    solvraConfig.get<string>('anthropicApiKey', '') ||
    solvraConfig.get<string>('openaiApiKey', '');
  const hasEnvKey =
    process.env.GOOGLE_API_KEY ||
    process.env.ANTHROPIC_API_KEY ||
    process.env.OPENAI_API_KEY;

  if (!hasSettingsKey && !hasEnvKey) {
    vscode.window
      .showWarningMessage(
        'Solvra: No API key configured. Set one in Settings to use the agent.',
        'Open Settings'
      )
      .then((selection) => {
        if (selection === 'Open Settings') {
          vscode.commands.executeCommand(
            'workbench.action.openSettings',
            'solvra api key'
          );
        }
      });
  }

  outputChannel.appendLine('Solvra extension activated.');
}

export function deactivate() {
  // Cleanup is handled by disposables
}
