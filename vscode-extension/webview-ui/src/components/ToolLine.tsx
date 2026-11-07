import { useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import type { Item } from '../store';
import type { EditInfo } from '../../../src/protocol';
import { post } from '../vscode';
import { Icon } from './Icon';
import { DiffView } from './DiffView';

type ToolItem = Extract<Item, { kind: 'tool' }>;

function splitSummary(summary: string): [string, string] {
  const i = summary.indexOf(' ');
  return i < 0 ? [summary, ''] : [summary.slice(0, i), summary.slice(i + 1)];
}

function meta(item: ToolItem): string {
  if (item.status === 'waiting') return 'waiting for approval';
  if (item.status === 'denied') return 'not run';
  if (item.status === 'running') return '';
  const m = item.meta;
  if (m && (m.added || m.removed)) return `+${m.added ?? 0} −${m.removed ?? 0}`;
  if (item.status === 'error') return 'failed';
  if (m && m.lines && (item.name === 'file_read' || item.name === 'bash' || item.name === 'grep' || item.name === 'glob')) return `${m.lines} line${m.lines === 1 ? '' : 's'}`;
  return '';
}

const pathOf = (item: ToolItem) => (typeof item.input.path === 'string' ? (item.input.path as string) : null);

export function ToolLine({ item, edit }: { item: ToolItem; edit?: EditInfo }) {
  const [open, setOpen] = useState(false);
  const [verb, target] = splitSummary(item.summary);
  const hasBody = !!(item.diff || item.output || (item.name === 'bash' && item.input.command));
  const dot = item.status === 'running' ? 'running' : item.status === 'waiting' ? 'waiting' : item.status === 'ok' ? 'ok' : item.status === 'denied' ? 'denied' : 'error';
  const path = pathOf(item);
  return (
    <div className={`tool ${item.status}`}>
      <div className="step-row">
        <button className="step-line" aria-expanded={hasBody ? open : undefined} disabled={!hasBody} onClick={() => setOpen(!open)}>
          <span className={`dot ${dot}`} aria-hidden="true" />
          <span className="verb">{verb}</span>
          <span className="target ellipsis" dir="auto">{target}</span>
          <span className={`meta ${item.meta?.added || item.meta?.removed ? 'diffstat' : ''}`}>{edit?.state === 'undone' ? 'undone' : edit?.state === 'kept' ? `${meta(item)} · kept` : meta(item)}</span>
          {hasBody && <Icon name={open ? 'chevron-down' : 'chevron-right'} className="dim chevron" />}
        </button>
        {path && (item.status === 'ok') && !edit && (
          <button className="icon-btn small reveal" title="Open file" aria-label={`Open ${path}`} onClick={() => post({ type: 'openFile', path })}><Icon name="go-to-file" /></button>
        )}
      </div>
      {edit && edit.state !== 'undone' && (
        <div className="edit-actions">
          <button className="link-btn" onClick={() => post({ type: 'openDiff', toolId: item.id })}><Icon name="diff" /> Review</button>
          {edit.state === 'pending' && <button className="link-btn" onClick={() => post({ type: 'keepEdit', toolId: item.id })}><Icon name="check" /> Keep</button>}
          <button className="link-btn" onClick={() => post({ type: 'undoEdits', toolIds: [item.id] })}><Icon name="discard" /> Undo</button>
        </div>
      )}
      {edit?.note && <div className="tool-error" dir="auto">{edit.note}</div>}
      <AnimatePresence initial={false}>
        {open && hasBody && (
          <motion.div className="tool-body" initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }}>
            {item.name === 'bash' && typeof item.input.command === 'string' && <pre className="cmd"><span className="dim">$ </span>{item.input.command as string}</pre>}
            {item.diff ? <DiffView diff={item.diff} /> : item.output ? <pre className="output" dir="auto">{item.output}</pre> : null}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
