/**
 * pick-model.ts — ⌘K model picker command.
 *
 * Registers `solvra.pickModel` which opens a VS Code QuickPick with rich
 * model info. Persists the choice to `solvra.model` (user setting) and
 * fires a callback so the chat webview can reflect it live.
 *
 * Wire it up in extension.ts:
 *   import { registerPickModel } from './pick-model';
 *   registerPickModel(context, () => {
 *     // optional: tell the chat webview to update its pill
 *     chatProvider.postMessage({ type: 'modelChanged' });
 *   });
 *
 * Keybinding (add to package.json contributes.keybindings):
 *   { "command": "solvra.pickModel", "key": "ctrl+k m", "mac": "cmd+k m" }
 *
 * Command (add to package.json contributes.commands):
 *   { "command": "solvra.pickModel", "title": "Solvra: Pick Model", "category": "Solvra" }
 */
import * as vscode from 'vscode';

interface ModelOption {
  id: string;
  label: string;
  description: string;   // shown right-aligned, small
  detail: string;        // shown on the second line, muted
  provider: 'anthropic' | 'openai' | 'local';
}

const MODELS: ModelOption[] = [
  {
    id: 'claude-sonnet-4',
    label: 'Sonnet 4',
    description: '$3 / $15 per 1M',
    detail: '200k ctx · balanced · recommended for most coding tasks',
    provider: 'anthropic',
  },
  {
    id: 'claude-sonnet-3.7',
    label: 'Sonnet 3.7',
    description: '$3 / $15 per 1M',
    detail: '200k ctx · previous generation, slightly faster',
    provider: 'anthropic',
  },
  {
    id: 'claude-haiku-4.5',
    label: 'Haiku 4.5',
    description: '$1 / $5 per 1M',
    detail: '200k ctx · fastest, cheapest — great for quick questions',
    provider: 'anthropic',
  },
  {
    id: 'claude-opus-4',
    label: 'Opus 4',
    description: '$15 / $75 per 1M',
    detail: '200k ctx · deepest reasoning, slowest — use for hard problems',
    provider: 'anthropic',
  },
  {
    id: 'gpt-5',
    label: 'GPT-5',
    description: '$5 / $15 per 1M',
    detail: '128k ctx · OpenAI — requires OPENAI_API_KEY',
    provider: 'openai',
  },
  {
    id: 'gpt-5-mini',
    label: 'GPT-5 mini',
    description: '$0.25 / $1 per 1M',
    detail: '128k ctx · OpenAI — cheap & fast',
    provider: 'openai',
  },
];

const PROVIDER_ICON: Record<ModelOption['provider'], string> = {
  anthropic: '$(sparkle)',
  openai:    '$(symbol-color)',
  local:     '$(server-environment)',
};

export function registerPickModel(
  context: vscode.ExtensionContext,
  onChanged?: (id: string) => void
): void {
  const disposable = vscode.commands.registerCommand('solvra.pickModel', async () => {
    const cfg = vscode.workspace.getConfiguration('solvra');
    const current = cfg.get<string>('model', 'claude-sonnet-4');

    const picker = vscode.window.createQuickPick();
    picker.title = 'Switch model';
    picker.placeholder = 'Type to filter — press ↵ to select';
    picker.matchOnDescription = true;
    picker.matchOnDetail = true;
    picker.items = MODELS.map(m => ({
      label: `${PROVIDER_ICON[m.provider]} ${m.label}${m.id === current ? '  $(check)' : ''}`,
      description: m.description,
      detail: m.detail,
      // Stash the id so we can retrieve on accept.
      id: m.id,
    } as vscode.QuickPickItem & { id: string }));

    picker.activeItems = picker.items.filter(
      (it: any) => it.id === current
    ) as vscode.QuickPickItem[];

    picker.onDidAccept(async () => {
      const [pick] = picker.selectedItems as (vscode.QuickPickItem & { id: string })[];
      picker.hide();
      if (!pick || pick.id === current) return;
      await cfg.update('model', pick.id, vscode.ConfigurationTarget.Global);
      vscode.window.setStatusBarMessage(`Solvra model → ${pick.id}`, 2500);
      onChanged?.(pick.id);
    });

    picker.onDidHide(() => picker.dispose());
    picker.show();
  });

  context.subscriptions.push(disposable);
}
