import { useEffect, useState } from 'react';
import type { KeyStatus, SettingsValues } from '../../../src/protocol';
import type { State } from '../store';
import { post } from '../vscode';
import { Icon } from './Icon';
import { InlineKey } from './Composer';

export function ViewHeader({ title, onBack }: { title: string; onBack: () => void }) {
  return (
    <div className="view-header">
      <button className="icon-btn" aria-label="Back to chat" title="Back to chat" onClick={onBack}><Icon name="arrow-left" /></button>
      <h2>{title}</h2>
    </div>
  );
}

const save = (patch: Partial<SettingsValues>) => post({ type: 'saveSettings', patch });

export function SettingsView({ state, onBack }: { state: State; onBack: () => void }) {
  const s = state.settings;
  if (!s) return <div className="view"><ViewHeader title="Settings" onBack={onBack} /><p className="dim pad">Loading…</p></div>;
  const v = s.values;
  const models = state.providers.flatMap((g) => g.models.map((m) => ({ ...m, group: g.label })));
  return (
    <div className="view scroll" onKeyDown={(e) => { if (e.key === 'Escape') onBack(); }}>
      <ViewHeader title="Settings" onBack={onBack} />
      <section className="settings-section">
        <h3>API keys</h3>
        <p className="dim small">Stored in VS Code’s secret storage on this machine, never in settings files. ChatGPT models use your Codex sign-in instead.</p>
        {s.keys.map((k) => <KeyRow key={k.provider} k={k} />)}
      </section>
      <section className="settings-section">
        <h3>Defaults for new messages</h3>
        <Field label="Default model" hint="Also changes when you pick a model in the chat.">
          <select value={v.model} onChange={(e) => save({ model: e.target.value })} aria-label="Default model">
            {!models.some((m) => m.id === v.model) && <option value={v.model}>{v.model}</option>}
            {state.providers.map((g) => (
              <optgroup key={g.provider} label={`${g.label}${g.ready ? '' : ' (not set up)'}`}>
                {g.models.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}
              </optgroup>
            ))}
          </select>
        </Field>
        <Field label="Effort" hint="How hard the model thinks. Higher is slower and costs more.">
          <div className="segmented" role="radiogroup" aria-label="Effort">
            {(['low', 'medium', 'high', 'xhigh'] as const).map((e) => (
              <button key={e} role="radio" aria-checked={v.effort === e} className={v.effort === e ? 'on' : ''} onClick={() => save({ effort: e })}>{e === 'xhigh' ? 'Extra high' : e[0].toUpperCase() + e.slice(1)}</button>
            ))}
          </div>
        </Field>
        <NumberField label="Max turns" hint="Model calls per message. 0 means no limit." value={v.maxTurns} step={1} onSave={(n) => save({ maxTurns: n })} />
        <NumberField label="Budget per message (USD)" hint="Stops a message that would cost more. 0 means no limit." value={v.maxBudget} step={0.05} onSave={(n) => save({ maxBudget: n })} />
        <Field label="Start new chats in Auto-approve" hint="Off: new chats start in Ask before edits, so nothing is edited or run without your approval.">
          <label className="toggle">
            <input type="checkbox" checked={v.autoApprove} onChange={(e) => save({ autoApprove: e.target.checked })} />
            <span>{v.autoApprove ? 'On' : 'Off'}</span>
          </label>
        </Field>
      </section>
      <section className="settings-section">
        <h3>Solvra backend</h3>
        <TextField label="Solvra location" hint="Folder with Solvra.sln or a built Solvra.dll. Empty: found automatically." value={v.solvraPath} placeholder="Found automatically" onSave={(t) => save({ solvraPath: t })}
          extra={<button className="btn ghost small" onClick={() => post({ type: 'browseExecutable' })}>Browse…</button>} />
        <TextField label=".NET executable" hint="Used to start the backend." value={v.dotnetPath} placeholder="dotnet" onSave={(t) => save({ dotnetPath: t })} />
      </section>
    </div>
  );
}

function KeyRow({ k }: { k: KeyStatus }) {
  const [editing, setEditing] = useState(false);
  const [confirm, setConfirm] = useState(false);
  return (
    <div className="key-row">
      <div className="grow">
        <div>{k.label}</div>
        <div className="dim small">{k.set ? 'Saved' : k.fromEnv ? 'From the environment' : 'Not set'} · <a href={k.url}>Get a key</a></div>
      </div>
      {confirm ? (
        <>
          <span className="small">Remove this key?</span>
          <button className="btn danger small" onClick={() => { post({ type: 'clearApiKey', provider: k.provider }); setConfirm(false); }}>Remove</button>
          <button className="btn ghost small" onClick={() => setConfirm(false)}>Cancel</button>
        </>
      ) : !editing && (
        <>
          <button className="btn ghost small" onClick={() => setEditing(true)}>{k.set ? 'Replace' : 'Add key'}</button>
          {k.set && <button className="icon-btn small" title="Remove key" aria-label={`Remove ${k.label} key`} onClick={() => setConfirm(true)}><Icon name="trash" /></button>}
        </>
      )}
      {editing && <div className="key-edit"><InlineKey provider={k.provider} label={k.label} onDone={() => setEditing(false)} /></div>}
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="field">
      <div className="field-label"><div>{label}</div>{hint && <div className="dim small">{hint}</div>}</div>
      <div className="field-control">{children}</div>
    </div>
  );
}

function NumberField({ label, hint, value, step, onSave }: { label: string; hint: string; value: number; step: number; onSave: (n: number) => void }) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  const commit = () => { const n = Number(text); if (Number.isFinite(n) && n >= 0 && n !== value) onSave(n); else setText(String(value)); };
  return (
    <Field label={label} hint={hint}>
      <input type="number" min={0} step={step} value={text} aria-label={label} onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
    </Field>
  );
}

function TextField({ label, hint, value, placeholder, onSave, extra }: { label: string; hint: string; value: string; placeholder: string; onSave: (t: string) => void; extra?: React.ReactNode }) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  const commit = () => { if (text !== value) onSave(text); };
  return (
    <Field label={label} hint={hint}>
      <div className="row">
        <input className="grow mono" value={text} placeholder={placeholder} aria-label={label} onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
        {extra}
      </div>
    </Field>
  );
}
