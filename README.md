# Solvra

Lightweight, powerful, secure AI agent orchestrator — built in C# (.NET 8).

A complete rewrite of [Altimeter](https://github.com/Movement-Create/Altimeter) in C#, preserving the same architecture and design philosophy.

## Design Philosophy

1. **Lightweight** — Minimal dependencies. Pure C#/.NET 8. The agent loop is a simple while loop.
2. **Powerful** — While-loop core, 18 built-in tools, subagent spawning, multi-provider LLM, skill injection, memory system.
3. **Secure** — Per-tool permission levels, process-level sandbox, dangerous command detection, audit logging.

## Quick Start

```bash
# Build
dotnet build

# Set your API key
export GOOGLE_API_KEY=your-key-here
# or
export ANTHROPIC_API_KEY=your-key-here
# or
export OPENAI_API_KEY=your-key-here

# Run
dotnet run --project src/Solvra -- run "What files are in this directory?"

# Interactive chat
dotnet run --project src/Solvra -- chat
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `solvra run <prompt>` | Run agent with a prompt |
| `solvra chat` | Interactive REPL mode |
| `solvra models` | List available models |
| `solvra serve` | Start webhook server |
| `solvra memory prune` | Prune old lessons |
| `solvra session list` | List past sessions |
| `solvra session show <id>` | Show session details |
| `solvra session resume <id>` | Continue a session in the chat REPL |
| `solvra session delete <id>` | Delete a session |

### Run Options

```
-p, --provider   LLM provider (anthropic, openai, google, ollama, moonshot, chatgpt).
                 An explicit provider always wins over guessing from the model name.
-m, --model      Model name; "provider:model" pins the provider (e.g. openai:qwen3.8-flash)
--max-turns      Maximum agent turns (default: config max_turns, 50)
--effort         Effort level: low, medium, high, max
--auto           Auto-approve all tool permissions
--plan           Plan mode: only read-only tools run; the agent describes its changes
--json           Output result as JSON (text, stop_reason, error, session_id, usage)
--system         System prompt (replaces the default coding instructions)
--session <id>   Continue (or create) a named session
--no-session     Don't write a session file
--cwd <dir>      Working directory for tools and project instructions
--max-budget     Max estimated USD per run (0 = no limit; unknown/subscription models cost 0)
--reflect        Run the post-task lesson-saving pass (off by default)
```

`solvra run -` reads the prompt from stdin. Exit codes: 0 done, 1 error, 2 turn limit, 3 budget limit.

### Chat

`solvra chat` supports `/model`, `/mode ask|auto|plan`, `/clear`, `/compact`, `/cost`, `/session`,
`/tools`. Ctrl+C interrupts the running turn. End a line with `\` to continue it, or wrap a
multi-line message in lines of `"""`.

### Project instructions

The system prompt includes the working directory, OS, date and git status, plus every
`AGENTS.md`, `CLAUDE.md`, `SOLVRA.md` and `.solvra.md` from the git root down to the working
directory (and `~/.config/solvra/SOLVRA.md` for user-wide rules).

## Supported Providers

| Provider | Models | Env Variable |
|----------|--------|-------------|
| Anthropic | claude-opus-5, claude-sonnet-5, claude-haiku-4-5 | `ANTHROPIC_API_KEY` |
| OpenAI (and OpenAI-compatible gateways via `OPENAI_BASE_URL`) | gpt-4.1, o3, any gateway model | `OPENAI_API_KEY` |
| Google | gemini-2.5-pro, gemini-2.5-flash | `GOOGLE_API_KEY` |
| Ollama | llama3.1, llama3.2, any local model | (local, no key needed) |

## Built-in Tools

| Tool | Permission | Description |
|------|-----------|-------------|
| `bash` | Execute | Run shell commands |
| `code_run` | Execute | Write + execute code (Python, Node, Bash, TypeScript) |
| `file_read` | Read | Read file contents |
| `file_write` | Write | Create/overwrite files |
| `file_edit` | Write | Search and replace in files |
| `glob` | Read | File pattern matching |
| `grep` | Read | Regex search in files |
| `web_fetch` | Network | Fetch URL content |
| `web_search` | Network | Web search via DuckDuckGo |
| `doc_create` | Write | Generate PDF/CSV documents |
| `spreadsheet_create` | Write | Create Excel spreadsheets |
| `csv_write` | Write | Write CSV files |
| `memory_note` | Write | Save facts/lessons to memory |
| `memory_recall` | Read | Search memory |
| `todo` | Write | Task tracking |
| `agent` | Agent | Spawn subagent |

## Architecture

```
Program.cs (CLI)
    │
    ├── Core/Reflection.cs     ← RunAgentWithReflection (wraps the loop)
    │       │
    │       └── Core/AgentLoop.cs     ← THE LOOP
    │               │
    │               ├── Providers/ModelRouter.cs     → provider selection
    │               │       └── Anthropic/OpenAI/Google/Ollama
    │               │
    │               ├── Tools/ToolRegistry.cs        → tool dispatch + permissions
    │               │       └── Bash/FileRead/Memory/...
    │               │
    │               ├── Hooks/HookEngine.cs          → PreToolUse/PostToolUse/Stop
    │               │
    │               └── Core/Context.cs              → system prompt + compression
    │                       ├── Skills/SkillLoader.cs
    │                       └── Memory/MemoryManager.cs
    │
    ├── Core/Session.cs        → JSONL session store
    │
    ├── Security/
    │   ├── PermissionChecker.cs
    │   ├── SandboxManager.cs
    │   ├── DangerousCommandDetector.cs
    │   └── AuditLogger.cs
    │
    ├── Scheduler/
    │   ├── CronScheduler.cs
    │   └── WebhookServer.cs
    │
    └── Config/ConfigLoader.cs

```

## Security

### Permission Levels (ascending risk)
`Read < Write < Network < Execute < Agent`

### Permission Modes
- **Default** — Read and Network tools run; Write, Execute and Agent tools ask first. With nobody
  to ask (no terminal), they are refused rather than silently allowed.
- **Auto** — Allow all (for headless/CI)
- **Plan** — Read-only: Write/Execute/Agent tools are refused and the agent describes its plan

Subagents inherit the parent's mode, approval prompt, working directory and tool lists; nesting
is capped at 2 levels.

### Sandbox
Process-level guard rails (not a security boundary):
- Per-command timeout (`timeout_ms`, default 120 s, max 10 min); stdin closed
- Output always drained (no pipe deadlocks); the model sees head+tail (~30k chars) and the full
  output is saved to a temp file
- Credential-looking environment variables (`*KEY*`, `*TOKEN*`, `*SECRET*`, …) are removed from
  tool processes; `SOLVRA_TOOL_ENV_PASSTHROUGH=NAME1,NAME2` keeps specific ones
- Dangerous command detection: recursive deletes of `/`, home, the working tree or system paths,
  disk writes, download-and-execute, `git push --force`, `git reset --hard`, `git clean -fd`, …
- `web_fetch` refuses loopback/private/metadata addresses (`SOLVRA_WEBFETCH_ALLOW_PRIVATE=1` to allow)

### Hooks
Shell-command hooks from config (`"hooks": {"PreToolUse": ["cmd"], "PostToolUse": [...], "Stop": [...]}`)
receive the event as JSON on stdin. Exit code 2 blocks the tool (stderr is the reason); a
`{"action":"modify","input":{...}}` reply on stdout rewrites the tool input.

### Webhook server
`solvra serve` binds 127.0.0.1 by default (`--host` to change) and refuses to start the webhook
without `SOLVRA_WEBHOOK_SECRET` unless `--insecure-no-auth` is given.

## State directories

Sessions, logs and memory live under `~/.local/share/solvra` (`$XDG_DATA_HOME/solvra`), never in
the project. Override with `SOLVRA_HOME`, or `SOLVRA_SESSIONS_DIR` / `SOLVRA_LOGS_DIR` /
`SOLVRA_MEMORY_DIR`.

## Memory

```
~/.local/share/solvra/memory/
├── facts.md      ← Persistent facts
├── lessons.md    ← Dated, tagged lessons from past sessions
└── 2024-01-15.md ← Daily logs
```

The agent uses `memory_note` and `memory_recall` tools to interact with memory. Lessons are relevance-scored and injected into the system prompt.

## Testing

```bash
dotnet test
```

314 tests covering core logic, the agent loop, providers, tools, security, sessions, skills and memory.

## License

MIT
