# Solvra harness fixes — progress

# Composability Part 1D — 2026-09-23

Worktree: `/home/cosmos/wt/github/Solvra/composability-1d`, branch
`composability-1d`, based on `origin/main` at `25db057`.

Prepared reversible component registration without changing the live service. Tool registration
now returns an idempotent `IDisposable`; disposing removes only the same tool instance, so a stale
handle cannot remove a forced replacement. The existing duplicate policy remains explicit
(`force: false` rejects, `force: true` replaces), and `Unregister(name)` removes the current owner.
Hook registration returns the same handle shape while preserving the existing behavior that
duplicate hook IDs may coexist; handle disposal removes one exact hook and `Unregister(id)` keeps
its existing remove-all behavior. Existing callers compile unchanged when they discard the return.

`SkillLoader.ReloadAsync()` and `Reload()` build a complete candidate snapshot, reject duplicate
or empty names, atomically swap only after validation, and return added/removed/changed names.
Explicit reload surfaces validation failures; periodic discovery retains the last valid snapshot.
There is still no watcher and no change to serve, cron, webhook, config or provider behavior.

Verification: all 327 tests pass. New coverage includes duplicate policy, double disposal, stale
handles, prior-state restoration, add/change/delete skill diffs and failed-snapshot rollback. An
isolated detailed run measured one skill add at 15.65 ms, change at 3.87 ms and delete at 5.52 ms;
10,000 tool+hook register/dispose pairs took 5.36 ms and retained 328 bytes after forced GC on this
run. A collectible `AssemblyLoadContext` loaded and released the Solvra assembly in isolation,
showing CLR unloadability; Solvra still lacks a plugin assembly contract and dependency boundary,
so that probe does not justify dynamic tool assemblies yet. No provider call or process restart
occurred, and the installed build plus `solvra.service` remain untouched.

Remaining: review and publish this branch under the repository's rules; use the Part 1 review to
choose whether Part 2 needs only registry/skill reload or a later isolated assembly plugin layer.

Branch `harness-fixes` (worktree `~/wt/github/Solvra/harness-fixes`), based on `zen-go-session` (ff83703).
Source of the issue list: `/tmp/solvra-eval/REPORT.md` (live evaluation on 2026-09-21).
Committed and pushed to main on 2026-09-21 at the user's request.

## Done

Providers / routing
- Explicit `--provider`, `provider:model`, or a configured provider (config/SOLVRA_PROVIDER) beats guessing
  from the model name (`ModelRouter.ChooseProvider`, `Resolve`). Colon only splits for registered providers
  (Ollama tags like `llama3.1:70b` work). Effort defaults updated (claude-sonnet-5/opus-5/haiku-4-5, gpt-4.1/o3).
- OpenAI streaming: tool calls buffered per index and emitted at stream end (qwen repeated-id bug), in-stream
  errors surfaced, usage requested (`stream_options.include_usage`, disable with SOLVRA_OPENAI_STREAM_USAGE=0)
  and read after finish_reason, `finish_reason: length` → `max_tokens`, `reasoning_content` captured and
  replayed, `max_completion_tokens` for o-series/gpt-5. Non-streaming: `tool_calls: null`, missing usage OK.
- Unparseable tool arguments become a tool error (not a silent `{}` call).
- Unknown models cost $0 (SOLVRA_PRICE_IN/OUT to price them); Claude prices/models updated.
- HTTP errors carry the provider message and Retry-After.

Agent loop
- Retries on the streaming path too; transport errors retried; quota/plan 429s not retried.
- Provider errors end the run with `StopReason.Error` + message (no unhandled exception); history stays valid.
- max_tokens replies auto-continued (2x); turn/budget limits reported in the text; budget checked after tool results.
- Consecutive read-only tool calls run in parallel (order preserved).
- System prompt: coding-agent instructions, environment (cwd, OS, date, git branch/status/log), project
  instructions from AGENTS.md/CLAUDE.md/SOLVRA.md/.solvra.md walking up to the git root + ~/.config/solvra/SOLVRA.md.
- Context compression counts system prompt + tool schemas, shortens only old tool results (head+tail),
  truncates at user-turn boundaries (never splits tool_use/tool_result).
- Session log written in order by the loop (`assistant_turn` events with text/reasoning/tool calls);
  resume tolerant of corrupt lines; session ids validated.
- Reflection pass opt-in (`reflection: true` / SOLVRA_REFLECTION=1 / `--reflect`), can't fail a run, not merged into history.

Tools / security
- bash: `timeout_ms` honoured (default 120 s, max 600 s), output always drained (1 MB pipe deadlock fixed),
  stdin closed, head+tail truncation (~30k chars) with full output saved under /tmp/solvra-tool-output,
  `background=true`, exit code shown. Secret-looking env vars stripped from tool processes
  (SOLVRA_TOOL_ENV_PASSTHROUGH to keep some).
- file_read: 1-based offset, 2000-line default, paging hint, long-line clipping, binary refusal, directory listing.
- file_edit: replace_all, CRLF/BOM preserved, snippet after edit, near-miss hints, create with empty old_string.
- grep: output modes (content/files/count), context, case-insensitive, file path support, missing path is an
  error, .gitignore + ignore dirs, limits. glob: anchored, `{a,b}`, newest first, limit, .gitignore.
- todo per session (+ set/clear). web_fetch: http(s) only, SSRF guard (loopback/private/metadata/CGNAT;
  SOLVRA_WEBFETCH_ALLOW_PRIVATE=1), manual redirects, 4xx/5xx are errors, body cap.
- code_run: script screened by the detector, filename sanitized, temp dir cleaned.
- Permissions: plan mode read-only; default mode asks for Write/Execute/Agent and refuses with no approver.
- Subagents inherit permission mode, approval prompt, cwd, provider, tool lists; depth cap enforced.
- Dangerous-command detector rewritten (catches rm -rf ~/./*, find / -delete, curl|sudo bash, bash -c "$(curl)",
  git push --force, git reset --hard, git clean -fd; no false positives on rm -f build/x.o, while true, eval ssh-agent).
- Webhook: loopback-only by default (enforced on the peer, so Tailscale Serve still works), refuses to start
  without a secret (--insecure-no-auth), constant-time secret compare.
- Config: shell-command hooks loaded (exit 2 blocks, JSON modify), JSON5 comment stripping no longer breaks
  URLs, broken config reported, `reflection`, `max_tokens`, `max_budget_usd` default $5.
- State (sessions/logs/memory) under ~/.local/share/solvra (SOLVRA_HOME etc.), never in the project.

CLI
- run: `--session` (continue/create), `--no-session`, `--cwd`, `--max-budget`, `--reflect`, prompt `-` from stdin,
  exit codes 0/1/2/3, JSON adds error/session_id/model/provider with readable escaping, no stack traces
  (SOLVRA_DEBUG=1 for them), tool progress shows name + argument summary, NO_COLOR/pipe aware.
- chat / session resume: shared REPL; Ctrl+C interrupts the turn; /model /mode /clear /compact /cost; multiline.
- NDJSON chat: no double logging, error turns flagged, plan mode, close/SIGTERM release pending permission prompts.

## Verification
- `dotnet test`: 316/316 pass (was 209). New suites in tests/Solvra.Tests/Harness/.
- End-to-end against a local mock gateway (qwen-style stream, first request 500, reasoning, usage):
  run fixed the fixture bug in 3 turns, retry worked, reasoning replayed, usage recorded, repo clean.
  NDJSON protocol run with permission prompts answered: OK.
- Deployed 2026-09-21 15:17/15:20 UTC to ~/.local/opt/solvra (backup: ~/.local/opt/solvra.bak-20260921-pre-harness-fixes),
  `systemctl --user restart solvra`: active; 401 without token on 127.0.0.1:7331 and on
  https://devbox.tail669189.ts.net:9477; 403 on the direct tailnet IP.
- Live model re-run blocked: opencode Go 5-hour quota exhausted (429, resets ~19:15 UTC). The new build reports it
  as a one-line error without retries.

## Live test on the ChatGPT subscription (chatgpt:<model>, 2026-09-21)
- Models available: gpt-6-astra, gpt-5.6-sol, gpt-5.6-terra, gpt-5.6-luna, gpt-5.5 (astra not used).
- luna bug fix: pass (9 turns). luna feature+log task: pass (355k input tokens → 202k after the file_read
  change below). sol feature+log task: pass (45k tokens, used grep). sol plan mode: no edits, correct diff proposed.
- Follow-up fixes: file_read char cap halved + "large file, use grep" hint; ChatGPT stop reasons normalized;
  ChatGPT stream parser tests; subscription cost label. Tests 316/316; redeployed.

## Scenario eval suite (evals/, 2026-09-21)
- 16 end-to-end cases with hidden checks (see evals/README.md); runner `evals/run.sh`.
- Dry run with a no-op agent: 0/16 pass (checks are meaningful).
- chatgpt:gpt-5.6-luna: 16/16 (case 05 initially failed on a check-script bug, re-scored after fixing the check;
  case 16 web_search run separately). All 16 tools exercised: bash, code_run, file_read/write/edit, glob, grep,
  web_fetch, web_search, csv_write,
  spreadsheet_create, doc_create, memory_note/recall, todo, agent (+ plan mode, headless refusal, sessions).
- chatgpt:gpt-5.6-sol on the 6 hardest coding cases (01 02 03 05 10 13): 6/6. The subagent depth cap was hit and
  handled cleanly in case 10.
- Harness change from this round: audit log uses readable JSON escaping.

## Remaining / next
- After the quota resets: re-run /tmp/solvra-eval (run.sh with template/template2) on glm-5.3-flash, minimax-m2.5,
  mimo-v2.5, qwen3.8-flash (no prefix needed now), kimi-k2.6, kimi-k3 (reasoning replay), gpt-5.6-luna.
- grok-4.6 on this gateway needs a non chat-completions format (not implemented).
- Not done: LLM-generated summaries during compaction (truncation + notice only), persistent shell cwd across
  bash calls, undo/checkpoints for edits, per-project memory namespaces.
- Rollback: `rm -rf ~/.local/opt/solvra && cp -a ~/.local/opt/solvra.bak-20260921-pre-harness-fixes ~/.local/opt/solvra && systemctl --user restart solvra`.
