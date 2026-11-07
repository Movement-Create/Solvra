import * as vscode from 'vscode';
import { AgentRunner } from './agent-runner';
import { SolvraChatProvider } from './chat-provider';
import { SolvraStatusBar } from './status-bar';
import { registerCommands } from './commands';
import { SessionsProvider } from './sessions-provider';
import { SessionPanelManager } from './session-panel-manager';
import { registerPickModel } from './pick-model';
import { KeyStore, migrateSettingsKeys } from './keys';
import { isV3, registerSelectionFeed, v3CommandOverrides, V3ViewProvider } from './v3-surfaces';
import type { V3Services } from './v3-controller';
import { CheckpointStore } from './checkpoints';
import { EditReview } from './edit-review';

export function activate(context: vscode.ExtensionContext) {
  const outputChannel = vscode.window.createOutputChannel('Solvra');
  context.subscriptions.push(outputChannel);

  // API keys live in SecretStorage. Move any plain-text keys older versions left in settings.json.
  // Never block activation on it: on Linux without a keyring, secret storage can be slow to answer.
  const keys = new KeyStore(context.secrets);
  void migrateSettingsKeys(vscode.workspace.getConfiguration('solvra'), context.secrets).then((report) => {
    if (report.migrated.length || report.removedFrom)
      outputChannel.appendLine(`[Solvra] Moved API keys to secret storage: ${report.migrated.join(', ') || 'none new'}; removed ${report.removedFrom} plain-text setting(s).`);
  }, (e) => outputChannel.appendLine(`[Solvra] API key migration skipped: ${e instanceof Error ? e.message : String(e)}`));

  const statusBar = new SolvraStatusBar();
  context.subscriptions.push({ dispose: () => statusBar.dispose() });
  const runner = new AgentRunner(outputChannel, keys, context.extensionPath);
  const checkpoints = new CheckpointStore(vscode.Uri.joinPath(context.globalStorageUri, 'checkpoints').fsPath);
  const review = new EditReview(checkpoints);
  context.subscriptions.push(review);
  const services: V3Services = { context, runner, keys, output: outputChannel, checkpoints, review };

  // The chat view: v3 (default) or the legacy UIs. Switching between them needs a window reload.
  const v3AtStart = isV3();
  const chatProvider = new SolvraChatProvider(context.extensionUri, runner, statusBar);
  context.subscriptions.push(vscode.window.registerWebviewViewProvider(
    SolvraChatProvider.viewType,
    v3AtStart ? new V3ViewProvider(services) : chatProvider,
    { webviewOptions: { retainContextWhenHidden: true } }));
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(async (e) => {
    if (!e.affectsConfiguration('solvra.ui.version') || isV3() === v3AtStart) return;
    const pick = await vscode.window.showInformationMessage('Reload the window to switch the Solvra chat UI.', 'Reload');
    if (pick === 'Reload') await vscode.commands.executeCommand('workbench.action.reloadWindow');
  }));

  if (v3AtStart) registerSelectionFeed(context);

  // Legacy sessions tree (hidden while v3 is active: the chat header's session menu replaces it).
  const sessionsProvider = new SessionsProvider(runner);
  chatProvider.setSessionsProvider(sessionsProvider);
  context.subscriptions.push(vscode.window.registerTreeDataProvider(SessionsProvider.viewType, sessionsProvider));
  const sessionPanelManager = new SessionPanelManager(context.extensionUri, runner, statusBar, sessionsProvider);

  const overrides = v3CommandOverrides(services);
  registerCommands(context, runner, chatProvider, statusBar, outputChannel, sessionsProvider, sessionPanelManager, overrides, isV3);
  registerPickModel(context, overrides['solvra.pickModel'], isV3);

  outputChannel.appendLine('Solvra extension activated.');
}

export function deactivate() {
  // Cleanup is handled by disposables
}
