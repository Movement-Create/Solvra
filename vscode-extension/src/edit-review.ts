// Review Solvra's edits in VS Code's own diff editor, with Keep and Undo in the editor title bar
// (and in the chat). The left side is the original saved before the edit.
import * as vscode from 'vscode';
import * as path from 'path';
import { CheckpointStore } from './checkpoints';

export const ORIGINAL_SCHEME = 'solvra-original';

export class EditReview implements vscode.Disposable {
  private readonly disposables: vscode.Disposable[] = [];
  private readonly changed = new vscode.EventEmitter<string[]>();
  /** Fired with tool ids whose state changed from the editor (so chats can update). */
  readonly onDidChange = this.changed.event;

  constructor(private readonly store: CheckpointStore) {
    this.disposables.push(
      vscode.workspace.registerTextDocumentContentProvider(ORIGINAL_SCHEME, {
        provideTextDocumentContent: (uri) => store.original(new URLSearchParams(uri.query).get('id') ?? ''),
      }),
      vscode.window.tabGroups.onDidChangeTabs(() => this.updateContext()),
      vscode.window.onDidChangeActiveTextEditor(() => this.updateContext()),
      // From the editor title bar VS Code passes the editor's URI; only a string is a tool id.
      vscode.commands.registerCommand('solvra.keepEdit', (id?: unknown) => this.keep(typeof id === 'string' ? id : this.activeId())),
      vscode.commands.registerCommand('solvra.undoEdit', (id?: unknown) => this.undoFromEditor(typeof id === 'string' ? id : this.activeId())),
      this.changed,
    );
  }

  dispose(): void { for (const d of this.disposables) d.dispose(); }

  async open(toolId: string): Promise<void> {
    const r = this.store.get(toolId);
    if (!r) return;
    const name = path.basename(r.file);
    const left = vscode.Uri.from({ scheme: ORIGINAL_SCHEME, path: '/' + name, query: `id=${encodeURIComponent(toolId)}` });
    const title = `${name} (${r.before ? 'before' : 'new file'} ↔ Solvra’s edit)`;
    await vscode.commands.executeCommand('vscode.diff', left, vscode.Uri.file(r.file), title, { preview: true });
    this.updateContext();
  }

  keep(toolId: string | undefined): void {
    if (!toolId || !this.store.keep(toolId)) return;
    this.changed.fire([toolId]);
    void this.closeDiff(toolId);
  }

  /** From the editor title bar: undo, asking first (natively) only when the file changed since. */
  private async undoFromEditor(toolId: string | undefined): Promise<void> {
    if (!toolId) return;
    let result = this.store.undo([toolId]);
    if (result.conflicts.length) {
      const pick = await vscode.window.showWarningMessage(
        `${path.basename(result.conflicts[0].file)} changed after Solvra edited it. Undo anyway and lose those changes?`, { modal: true }, 'Undo anyway');
      if (pick !== 'Undo anyway') return;
      result = this.store.undo([toolId], true);
    }
    this.changed.fire(result.undone);
    await this.closeDiff(toolId);
  }

  private activeId(): string | undefined {
    const input = vscode.window.tabGroups.activeTabGroup.activeTab?.input;
    if (input instanceof vscode.TabInputTextDiff && input.original.scheme === ORIGINAL_SCHEME)
      return new URLSearchParams(input.original.query).get('id') ?? undefined;
    return undefined;
  }

  private updateContext(): void {
    const id = this.activeId();
    const pending = !!id && this.store.get(id)?.state === 'pending';
    void vscode.commands.executeCommand('setContext', 'solvra.reviewingEdit', pending);
  }

  private async closeDiff(toolId: string): Promise<void> {
    for (const group of vscode.window.tabGroups.all)
      for (const tab of group.tabs) {
        const input = tab.input;
        if (input instanceof vscode.TabInputTextDiff && input.original.scheme === ORIGINAL_SCHEME && new URLSearchParams(input.original.query).get('id') === toolId)
          await vscode.window.tabGroups.close(tab);
      }
    this.updateContext();
  }
}
