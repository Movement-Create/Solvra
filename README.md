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

### Run Options

```
--provider    LLM provider (anthropic, openai, google, ollama)
--model       Model name (claude-3-5-sonnet-20241022, gpt-4o, gemini-2.5-flash, etc.)
--max-turns   Maximum agent turns (default: 100)
--effort      Effort level: low, medium, high, max
--auto        Auto-approve all tool permissions
--plan        Dry-run mode (no actual execution)
--json        Output result as JSON
--system      Custom system prompt
```

## Supported Providers

| Provider | Models | Env Variable |
|----------|--------|-------------|
| Anthropic | claude-3-5-sonnet, claude-3-5-haiku, claude-3-opus | `ANTHROPIC_API_KEY` |
| OpenAI | gpt-4o, gpt-4o-mini, o1-preview, o1 | `OPENAI_API_KEY` |
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
- **Default** — Ask before Execute/Agent level tools
- **Auto** — Allow all (for headless/CI)
- **Plan** — Dry-run (tools describe what they would do)

### Sandbox
Process-level isolation with:
- Timeout enforcement
- Output size cap (1MB)
- Environment variable allowlist
- Working directory restriction
- Dangerous command detection (19 regex patterns)

## Memory

```
memory/
├── facts.md      ← Persistent facts
├── lessons.md    ← Dated, tagged lessons from past sessions
└── 2024-01-15.md ← Daily logs
```

The agent uses `memory_note` and `memory_recall` tools to interact with memory. Lessons are relevance-scored and injected into the system prompt.

## Testing

```bash
dotnet test
```

143 tests covering core logic, tools, security, skills, and memory.

## License

MIT
