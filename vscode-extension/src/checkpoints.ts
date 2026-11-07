// Checkpoints for Solvra's edits: the backend saves each file's original before an edit
// (`--checkpoint-dir`); this store remembers what the edit produced so it can be reviewed, kept or
// undone later, alone or as "restore to before this message". No `vscode` import (unit-tested).
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import type { EditInfo, EditState } from './protocol';

export interface EditRecord {
  toolId: string;
  sessionId: string | null;
  file: string;
  /** Saved original, or null when the edit created the file. */
  before: string | null;
  /** sha256 of the file right after the edit (null: the file did not exist). */
  afterSha: string | null;
  state: EditState;
  at: number;
}

export interface UndoResult {
  undone: string[];
  /** Files changed since Solvra's edit: not undone unless forced. */
  conflicts: { toolId: string; file: string }[];
  failed: { toolId: string; message: string }[];
}

export function sha256(file: string): string | null {
  try { return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'); } catch { return null; }
}

export class CheckpointStore {
  private records = new Map<string, EditRecord>();
  private readonly indexFile: string;

  constructor(readonly dir: string) {
    this.indexFile = path.join(dir, 'index.json');
    try {
      const rows = JSON.parse(fs.readFileSync(this.indexFile, 'utf8')) as EditRecord[];
      for (const r of rows) this.records.set(r.toolId, r);
    } catch { /* first use */ }
  }

  private save(): void {
    fs.mkdirSync(this.dir, { recursive: true });
    fs.writeFileSync(this.indexFile, JSON.stringify([...this.records.values()], null, 1));
  }

  /** Call when the backend reports a finished edit; hashes the file as Solvra left it. */
  record(toolId: string, sessionId: string | null, file: string, before: string | null, now = Date.now()): EditRecord {
    const r: EditRecord = { toolId, sessionId, file, before, afterSha: sha256(file), state: 'pending', at: now };
    this.records.set(toolId, r);
    this.save();
    return r;
  }

  get(toolId: string): EditRecord | undefined { return this.records.get(toolId); }

  forSession(sessionId: string): Record<string, EditInfo> {
    const out: Record<string, EditInfo> = {};
    for (const r of this.records.values()) if (r.sessionId === sessionId) out[r.toolId] = { file: r.file, state: r.state };
    return out;
  }

  /** The original content, '' for a file the edit created. */
  original(toolId: string): string {
    const r = this.records.get(toolId);
    if (!r?.before) return '';
    try { return fs.readFileSync(r.before, 'utf8'); } catch { return ''; }
  }

  keep(toolId: string): boolean {
    const r = this.records.get(toolId);
    if (!r) return false;
    r.state = 'kept';
    this.save();
    return true;
  }

  /**
   * Undo edits, newest first, so several edits to one file unwind in order. An edit whose file
   * changed since Solvra wrote it (by the user, or a later edit not in the list) is a conflict and
   * is skipped unless `force`.
   */
  undo(toolIds: string[], force = false): UndoResult {
    const result: UndoResult = { undone: [], conflicts: [], failed: [] };
    const records = toolIds.map((id) => this.records.get(id)).filter((r): r is EditRecord => !!r && r.state !== 'undone')
      .sort((a, b) => b.at - a.at);
    // Check every edit against the state it should find once the newer ones are undone.
    if (!force) {
      const expected = new Map<string, string | null>(); // file → sha it will have when we reach the next record
      for (const r of records) {
        const current = expected.has(r.file) ? expected.get(r.file)! : sha256(r.file);
        if (current !== r.afterSha) result.conflicts.push({ toolId: r.toolId, file: r.file });
        expected.set(r.file, r.before ? sha256(r.before) : null);
      }
      if (result.conflicts.length) return result;
    }
    for (const r of records) {
      try {
        if (r.before) {
          fs.mkdirSync(path.dirname(r.file), { recursive: true });
          fs.copyFileSync(r.before, r.file);
        } else if (fs.existsSync(r.file)) {
          fs.rmSync(r.file);
        }
        r.state = 'undone';
        result.undone.push(r.toolId);
      } catch (e) {
        result.failed.push({ toolId: r.toolId, message: e instanceof Error ? e.message : String(e) });
      }
    }
    this.save();
    return result;
  }
}
