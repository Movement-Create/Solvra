// Pure helpers for building the `solvra run` command line. No `vscode` import so they can be
// unit-tested with plain Node (see test/run-args.test.js).

/** The three chat modes the UI offers. */
export type ChatMode = 'ask' | 'plan' | 'auto';

export const CHAT_MODES: readonly ChatMode[] = ['ask', 'plan', 'auto'];

/**
 * Normalise anything the webview, settings or older UI versions might send into a ChatMode.
 * Unknown or legacy values ("agent", "edit", "default", "") fall back to `fallback`, which
 * itself defaults to the safe choice, "ask".
 */
export function normalizeMode(value: unknown, fallback: ChatMode = 'ask'): ChatMode {
  const v = typeof value === 'string' ? value.trim().toLowerCase() : '';
  switch (v) {
    case 'ask':
    case 'default':
      return 'ask';
    case 'plan':
      return 'plan';
    case 'auto':
    case 'auto-approve':
      return 'auto';
    default:
      return fallback;
  }
}

/**
 * Backend permission flags for a chat mode.
 *
 * - ask:  Read and network tools run; write, execute and agent tools need approval. The
 *         extension has no approval channel yet, so the backend refuses them (fail closed).
 * - plan: Read-only. Never paired with --auto.
 * - auto: Everything runs without asking.
 *
 * The mode is always passed explicitly so a `permission_mode` in the user's Solvra config
 * cannot silently turn ask mode into auto.
 */
export function permissionArgs(mode: ChatMode): string[] {
  switch (mode) {
    case 'auto':
      return ['--permission-mode', 'auto', '--auto'];
    case 'plan':
      return ['--permission-mode', 'plan', '--plan'];
    case 'ask':
    default:
      return ['--permission-mode', 'default'];
  }
}

export interface RunArgsInput {
  dll: string;
  prompt: string;
  provider: string;
  model: string;
  maxTurns: number;
  mode: ChatMode;
  sessionId?: string;
}

export function buildRunArgs(input: RunArgsInput): string[] {
  const args = [
    input.dll,
    'run',
    input.prompt,
    '--provider', input.provider,
    '--model', input.model,
    '--max-turns', String(input.maxTurns),
    ...permissionArgs(input.mode),
  ];
  if (input.sessionId) args.push('--session', input.sessionId);
  return args;
}

/** Marker the backend writes to stderr when it refuses a tool because nobody can approve it. */
export const APPROVAL_NEEDED_MARKER = '[solvra:approval-needed]';

/** Tool names the backend refused for lack of an approver, in order, without duplicates. */
export function parseApprovalNeeded(stderr: string): string[] {
  const tools: string[] = [];
  for (const line of stderr.split(/\r?\n/)) {
    const i = line.indexOf(APPROVAL_NEEDED_MARKER);
    if (i < 0) continue;
    const name = line.slice(i + APPROVAL_NEEDED_MARKER.length).trim().split(/\s+/)[0];
    if (name && !tools.includes(name)) tools.push(name);
  }
  return tools;
}

export const APPROVAL_NEEDED_NOTICE =
  'Solvra needs your approval for edits. Approvals arrive in the next update; switch to Auto to allow edits now.';
