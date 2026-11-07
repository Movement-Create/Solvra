// How a chat message and its chips become the prompt sent to the backend. No `vscode` import.
// The backend keeps the conversation (--session), so only the new message is sent.
import type { Chip } from './protocol';

const MAX_SELECTION = 20000;

function lines(c: Chip): string {
  if (!c.startLine) return '';
  return c.endLine && c.endLine !== c.startLine ? `${c.startLine}-${c.endLine}` : `${c.startLine}`;
}

export function chipLabel(c: Chip): string {
  const l = lines(c);
  return l ? `${c.path}:${l}` : c.path;
}

export function buildPrompt(text: string, chips: Chip[]): string {
  const parts: string[] = [];
  const files = chips.filter((c) => c.kind === 'file').map((c) => c.path);
  const folders = chips.filter((c) => c.kind === 'folder').map((c) => c.path);
  if (files.length) parts.push(`Files the user attached (read them as needed): ${files.join(', ')}`);
  if (folders.length) parts.push(`Folders the user attached (explore them as needed): ${folders.join(', ')}`);
  for (const c of chips.filter((c) => c.kind === 'selection')) {
    const body = (c.text ?? '').length > MAX_SELECTION ? (c.text ?? '').slice(0, MAX_SELECTION) + '\n…(truncated)' : c.text ?? '';
    parts.push(`<selection path="${c.path}" lines="${lines(c)}">\n${body}\n</selection>`);
  }
  const msg = text.trim();
  if (msg) parts.push(msg);
  return parts.join('\n\n');
}

/** Title for a new session: the user's words, never the attachments wrapped around them. */
export function titleFrom(text: string, chips: Chip[]): string {
  const first = text.split('\n').map((l) => l.trim()).find((l) => l.length > 0);
  if (first) return first.replace(/\s+/g, ' ').slice(0, 60);
  const c = chips[0];
  return c ? `About ${chipLabel(c)}` : 'Untitled';
}
