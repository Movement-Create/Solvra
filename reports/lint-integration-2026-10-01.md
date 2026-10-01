# Lint integration implementation report

Date: 2026-10-01
Branch: `feature/lint-integration`
Base: `origin/main` at `fe99c81`
Contract: Movement-Create/Lint `docs/SOLVRA-REQUESTS.md`

## Result

Solvra now accepts screenshots directly through `chat --ndjson`, tells clients which models support
vision, rejects unsupported image input before a turn begins, provides strict approvals, and supports
tool-free ephemeral answers. The protocol also reports the resolved model, final status, usage and
machine-readable error codes.

## Architecture changes

- **Protocol boundary:** `send.images` is validated before queuing. PNG, JPEG and WebP payloads are
  checked for MIME, base64 validity, file signature, count (four) and decoded size (20 MiB each).
- **Message model:** the initial user turn can carry the existing provider-neutral `ImageContent`.
  Rich user messages also round-trip through persisted sessions.
- **Provider adapters:** OpenAI, ChatGPT and Anthropic already supported the shared image block.
  Gemini now emits `inlineData`/`fileData`; Ollama vision models emit the native `images` array.
- **Capability gate:** `ModelCapabilities` supplies conservative provider/model vision metadata.
  `models --json` returns `{id,label,vision}` objects with provider-qualified ids. NDJSON rejects an
  image sent to `vision:false` before emitting `start`.
- **Safe execution:** `AskAll` allows only local `file_read`, `glob` and `grep` without approval.
  All other registered tools, including network and future MCP tools, wait for permission.
- **Generation isolation:** `NoTools` sends no tool definitions and bypasses hooks, reflection,
  skills, memory, project instructions and user instruction files. Inline-image turns use this same
  isolation automatically, so text inside screenshots cannot induce a tool call. A later text-only
  turn can act after the user explicitly requests it.
- **Ephemeral state:** ephemeral runs use an empty session path and do not write user, assistant or
  result events. Cost telemetry remains separate from the conversation transcript.
- **File fallback:** `file_read` attaches PNG/JPEG/WebP/GIF data to model context for vision models,
  with the same size boundary, instead of treating the file as unreadable binary data.

## Acceptance results

| Test | Result | Evidence |
|---|---|---|
| A — visual probe | Pass | `gpt-5.6-luna` identified red left, blue right and a yellow circle on the right in one turn; zero tool events. |
| B — text-only model | Pass | `llama3.1` emitted `unsupported_input`; no `start` event. |
| C — strict write | Pass | `file_write` emitted permission while `probe.txt` did not exist; denial left it absent. |
| D — strict web | Pass | `web_fetch` emitted permission before the request; denial prevented the fetch. |
| E — no tools | Pass | Shell request received an explicit no-shell response; zero tool events. |
| F — ephemeral | Pass | Ready reported no session/file and no transcript was created. |
| G — image injection | Pass | Image text was described as untrusted content; the final isolated image turn emitted zero tool events. |

## Verification

- `dotnet test Solvra.sln --no-restore`: **361/361 passed**.
- `git diff --check`: passed.
- Provider JSON checks: Gemini `inlineData`, Ollama `images`, existing OpenAI/ChatGPT/Anthropic paths.
- `models --json --provider chatgpt`: provider-qualified ids and `vision:true` for available models.
- Live acceptance calls used `chatgpt:gpt-5.6-luna`; they did not mutate production configuration.
- Production releases use the commit-specific marker in `~/.local/opt/solvra/current` as the source
  of truth for the deployed revision.
