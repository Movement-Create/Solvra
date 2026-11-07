// Shared types for the v3 chat: backend stream-json events, and the messages exchanged between the
// extension host and the webview. Type-only: imported by both src/ and webview-ui/.

export type ChatMode = 'ask' | 'plan' | 'auto';
export type ToolKind = 'read' | 'edit' | 'execute' | 'agent' | 'network';
export type Decision = 'allow' | 'allow_always' | 'deny';
export type FixKind = 'add_api_key' | 'rate_limited' | 'retry' | 'set_executable' | 'install_dotnet' | 'codex_login';

// ── Backend events (`solvra run --output-format stream-json`, see docs/stream-json.md) ──

export interface SessionEvent { type: 'session'; id: string | null; title: string; model: string; provider: string; cwd: string; mode: string; effort: string; resumed: boolean; rules: string[]; additional_dirs?: string[] }
export interface TextDeltaEvent { type: 'text_delta'; agent: string; text: string }
export interface ThinkingDeltaEvent { type: 'thinking_delta'; agent: string; text: string }
export interface ToolCallEvent { type: 'tool_call'; agent: string; id: string; name: string; input: Record<string, unknown>; summary: string; kind: ToolKind }
export interface PermissionRequestEvent { type: 'permission_request'; id: string; agent: string; tool_call_id: string; tool: string; kind: ToolKind; summary: string; diff: string | null; command: string | null; always: string | null }
export interface PermissionRuleEvent { type: 'permission_rule'; rule: string; kind: string; prefix: string | null }
export interface ToolResultMeta { added: number | null; removed: number | null; lines: number }
/** Where an edit's original was saved (`before` null: the file was new). Only with --checkpoint-dir. */
export interface Checkpoint { file: string; before: string | null }
export interface ToolResultEvent { type: 'tool_result'; agent: string; id: string; ok: boolean; output: string; diff: string | null; meta: ToolResultMeta; checkpoint?: Checkpoint | null }
export interface AgentStartEvent { type: 'agent_start'; agent: string; parent: string; tool_call_id: string | null; title: string }
export interface AgentEndEvent { type: 'agent_end'; agent: string; status: 'ok' | 'error' | 'cancelled' }
export interface PlanItem { text: string; status: 'done' | 'active' | 'pending' }
export interface PlanEvent { type: 'plan'; agent: string; items: PlanItem[] }
export interface UsageEvent { type: 'usage'; agent: string; input: number; output: number; cost_usd: number; context_pct: number }
export interface ErrorEvent { type: 'error'; message: string; fix: FixKind | null; provider?: string | null }
export interface DoneEvent { type: 'done'; stop_reason: string; text: string; error: string | null; session_id: string | null; turns: number; duration_ms: number; cost_usd: number; usage: { input: number; output: number } }

export type StreamEvent =
  | SessionEvent | TextDeltaEvent | ThinkingDeltaEvent | ToolCallEvent | PermissionRequestEvent | PermissionRuleEvent
  | ToolResultEvent | AgentStartEvent | AgentEndEvent | PlanEvent | UsageEvent | ErrorEvent | DoneEvent;

// ── Shared view models ──

export interface Chip {
  kind: 'selection' | 'file' | 'folder';
  /** Workspace-relative path (or absolute when outside the workspace). */
  path: string;
  label: string;
  startLine?: number;
  endLine?: number;
  /** Selected text for selection chips. */
  text?: string;
  /** Absolute path, when known (needed for folders outside the first workspace folder). */
  abs?: string;
}

export interface SessionRow { id: string; title: string; created_at: string; updated_at: string; model: string; provider: string }

export interface TranscriptTool { id: string; name: string; input: Record<string, unknown>; output: string | null; ok: boolean | null }
export interface TranscriptMessage { role: 'user' | 'assistant'; text: string; tools: TranscriptTool[] }

export interface ModelOption { id: string; label: string; provider: string }
export interface ProviderGroup {
  provider: string;
  label: string;
  /** How the provider authenticates: an API key we store, a Codex sign-in, or nothing (local). */
  auth: 'key' | 'codex' | 'none';
  ready: boolean;
  models: ModelOption[];
}

export interface KeyStatus { provider: string; label: string; set: boolean; fromEnv: boolean; url: string }

export interface SettingsValues {
  model: string;
  effort: 'low' | 'medium' | 'high' | 'xhigh';
  maxTurns: number;
  maxBudget: number;
  autoApprove: boolean;
  /** solvra.executablePath: Solvra.dll, the solvra command, or a folder; empty = automatic. */
  executablePath: string;
  dotnetPath: string;
}

export interface SettingsState { values: SettingsValues; keys: KeyStatus[] }

export interface ToolInfo { name: string; description: string; kind: ToolKind }

export interface FileItem { path: string; label: string; kind: 'file' | 'folder'; abs?: string }

export type EditState = 'pending' | 'kept' | 'undone';
export interface EditInfo { file: string; state: EditState; note?: string }

export interface InitState {
  mode: ChatMode;
  model: string;
  settings: SettingsState;
  providers: ProviderGroup[];
  sessions: SessionRow[];
  workspaceName: string | null;
  /** True when shown in an editor tab rather than the sidebar. */
  inTab: boolean;
}

// ── Host → webview ──

export type HostMessage =
  | { type: 'init'; state: InitState }
  | { type: 'runStarted'; runId: string; text: string; chips: Chip[] }
  | { type: 'stream'; runId: string; event: StreamEvent }
  | { type: 'runEnded'; runId: string; exitCode: number | null }
  | { type: 'sessions'; sessions: SessionRow[] }
  | { type: 'sessionLoaded'; id: string; title: string; messages: TranscriptMessage[] }
  | { type: 'sessionRenamed'; id: string; title: string }
  | { type: 'newChat' }
  | { type: 'settings'; settings: SettingsState }
  | { type: 'providers'; providers: ProviderGroup[] }
  | { type: 'addChip'; chip: Chip }
  /** The editor's current selection, offered as an automatic chip (null when nothing is selected). */
  | { type: 'selection'; chip: Chip | null }
  | { type: 'prefill'; text: string; send?: boolean }
  | { type: 'fileResults'; query: string; items: FileItem[] }
  | { type: 'memoryResults'; query: string; results: string[]; added?: boolean }
  | { type: 'tools'; tools: ToolInfo[] }
  | { type: 'hostError'; message: string; fix?: FixKind | null; provider?: string | null }
  | { type: 'notice'; text: string }
  | { type: 'open'; view: 'settings' | 'memory' | 'tools' | 'sessions' | 'model' }
  | { type: 'focusInput' }
  /** Review state of edits that have a checkpoint (live ones and those restored with a session). */
  | { type: 'edits'; edits: Record<string, EditInfo> }
  /** Undo stopped because these files changed after Solvra edited them; the user may force it. */
  | { type: 'undoConflict'; toolIds: string[]; files: string[] };

// ── Webview → host ──

export type WebviewMessage =
  | { type: 'ready' }
  | { type: 'send'; text: string; chips: Chip[]; mode: ChatMode; model: string }
  | { type: 'cancel' }
  | { type: 'permission'; id: string; decision: Decision; message?: string }
  | { type: 'newChat' }
  | { type: 'openSession'; id: string }
  | { type: 'renameSession'; id: string; title: string }
  | { type: 'deleteSession'; id: string }
  | { type: 'refreshSessions' }
  | { type: 'setMode'; mode: ChatMode }
  | { type: 'setModel'; model: string }
  | { type: 'saveSettings'; patch: Partial<SettingsValues> }
  | { type: 'setApiKey'; provider: string; key: string }
  | { type: 'clearApiKey'; provider: string }
  | { type: 'searchFiles'; query: string }
  | { type: 'memoryAdd'; text: string }
  | { type: 'memorySearch'; query: string }
  | { type: 'listTools' }
  | { type: 'refreshProviders' }
  | { type: 'openFile'; path: string; line?: number }
  | { type: 'openInTab' }
  | { type: 'browseExecutable' }
  | { type: 'runFix'; fix: FixKind; provider?: string | null }
  | { type: 'openDiff'; toolId: string }
  | { type: 'keepEdit'; toolId: string }
  /** Undo edits newest first; `force` also undoes files changed since the edit. */
  | { type: 'undoEdits'; toolIds: string[]; force?: boolean }
  | { type: 'addFolder' };
