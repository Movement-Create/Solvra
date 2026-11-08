# Windows installer encoding fix 0.4.1 — 2026-11-08

Task worktree: windows-installer-encoding; branch fix/windows-installer-encoding; base origin/main f69c706.

- User hit a parser failure in Windows PowerShell 5.1: UTF-8 without BOM was decoded as ANSI.
- Installer messages now use ASCII; packaged PowerShell scripts explicitly include a UTF-8 BOM.
- CI builds the actual bundle and tests it using Windows PowerShell 5.1, matching the double-click launcher.
- Source dcee5a1: release packages rebuilt with matching embedded revision; internal checksums and 551-member device credential scan pass.
- GitHub runs 42468187045 and 42468202699 pass. Packaged scripts parsed and helper checks ran under Windows PowerShell 5.1.26100.33648; native worker registration succeeded using powershell.exe.
- Seven telemetry tests pass on each OS, ten receiver tests pass, and real public uploads pass from Windows/macOS/Linux.
- Original ANSI decoding reproduces six parser errors; fixed source yields zero. Regression guard rejects ZIPs with stripped BOMs.
- Release assets: solvra-windows-0.4.1.zip, solvra-macos-0.4.1.tar.gz, solvra-vscode-0.4.1.vsix, SHA256SUMS. Publication location: https://github.com/Movement-Create/Solvra/releases/tag/v0.4.1
- Native checks cover parsing, installer helpers and telemetry registration; interactive prerequisite downloads/ChatGPT sign-in were not repeated on the user's PC.
- Telemetry worker registration now also executes through Windows PowerShell 5.1 in CI.
- No changes to collector settings or device credentials.

---

# Public telemetry 0.4.0 — 2026-11-08

Base: origin/main e77baeb; task worktree public-telemetry, branch feat/public-telemetry, PR #11.
User authorized public collection independent of devbox and Mac/Windows release publication.

## Implemented and deployed
- Durable uploader with atomic pending batch/cursors and per-user background workers on both platforms.
- Full JSONL records encrypted on the device (RSA-OAEP-SHA256 + AES-256-GCM); private key stays under ~/env.
- Public Modal receiver live: https://lookingto3--solvra-telemetry-web.us-east.modal.run
- Ciphertext in existing Scaleway bucket; allowlisted metadata logs/traces forwarded to Grafana Cloud.
- Automatic graph timer imports immutable archived batches transactionally into Neo4j, including decisions/evidence links.
- Private per-device setup files served only to the owning Tailscale login on port 9597, expiring after seven days.
- Private key and device configs backed up through existing encrypted devbox-lake secrets backup.
- Public artifacts contain no device credentials. Source main checkout unchanged.

## Verification
- 466 backend tests, including 7 new sender tests; 27 extension tests; 10 Python receiver/enrollment/projection tests passed.
- GitHub run 42460532364 passed on Linux, Windows and macOS, including real public upload from every runner without Tailscale.
- CI's three synthetic decisions automatically reached Neo4j through the graph timer.
- Live archive decryption, graph replay deduplication, authentication rejection and historical backfill verified.
- Native Windows scheduled-task registration and macOS LaunchAgent definition validation passed.
- Private setup downloads verified over Tailscale; unauthorized requests rejected; archive key cannot be served.
- Staged changes scanned with gitleaks: no leaks. Release packaging produces Mac tar.gz, Windows zip, VSIX and checksums.

## Operational limits
- User must import the private device file; workers run while signed in. Retain local source files until delivery.
- Graph processing/viewing remains on devbox and catches up after downtime; public receipt does not depend on it.
- Grafana ingestion acknowledgments verified. Saved dashboard service-account credential returned 401, so dashboard readback was unavailable.
- Delivery is at least once; identifiers permit log deduplication. Single records over 8 MiB pause upload rather than lose content.
- Ephemeral runs have no session transcript; only recorded decisions can appear in the graph.
- No check has run on the user's own computers yet; native CI and actual public delivery were tested.

---

# Release 0.3.0 — 2026-11-07

Branch `release/0.3.0` (worktree `release-0.3`), PR #7 on top of #6. The user authorized merging
#3–#7 and publishing.

- Backend on Windows: `ShellLocator` finds bash (Git for Windows; WSL's bash is skipped) and
  `codex` (`codex.exe`, or npm's `codex.cmd` through `cmd.exe` with fixed arguments). Version 0.3.0.
- Extension: `backend-locator.ts` searches `solvra.executablePath` (DLL, wrapper or folder; never a
  `.cmd`), `solvraPath`, the bundled `backend/`, the installed location, then dev builds. It also
  finds `dotnet` without PATH. ChatGPT is the default provider. "Sign in with Codex" installs the
  CLI when missing.
- Packaging: `vscode-extension/scripts/package.sh` builds a VSIX with the backend inside.
  `scripts/build-release.sh` builds the VSIX, the macOS tar.gz and the Windows zip, plus SHA256SUMS.
- macOS `install.sh` is one step: it sets up ChatGPT (and installs the Codex CLI) by default.
  Windows: `install.ps1` plus "Install Solvra.cmd" (.NET, Git, Node/Codex, VS Code settings).
- Verified on the devbox: the macOS installer in a throwaway HOME (OK from ChatGPT); the
  installed VSIX in a clean VS Code (bundled backend, ChatGPT answered); 459 .NET, 24 host,
  13 webview tests; pwsh checks of `install.ps1` helpers; the main live flow.
- **Not verified on real macOS or Windows.** Untested there: `osascript` settings, the Windows
  junction, winget, `dotnet-install.ps1`, `code.cmd`, `cmd.exe` launch of `codex.cmd`, Git Bash.

---

# VS Code extension fix and redesign: Phases 0–3 — 2026-11-07

Stacked PRs, each with its own worktree under `/home/cosmos/wt/github/Solvra/`:

| PR | Branch (base) | Worktree | What |
|---|---|---|---|
| #3 | `fix/ask-mode-safety` (main) | `ask-mode-safety` | Ask never passes `--auto`; plan never pairs with it; `--permission-mode`; in-chat approval notice |
| #4 | `feature/stream-json` (#3) | `stream-json` | `run --output-format stream-json`, stdin approvals, nested parallel sub-agents, plan/usage events, session titles |
| #5 | `feature/webview-v3` (#4) | `webview-v3` | React v3 chat (default), everything in-panel, SecretStorage keys + migration, palette shows only Open Chat |
| #6 | `feature/vscode-integration` (#5) | `vscode-integration` | Checkpoints, native diff Keep/Undo, restore-to-message, every folder as context |

Verification on #6: 455/455 .NET, 18 host, 13 webview tests; a 24-state screenshot harness; live
VS Code 1.143.1 runs over CDP (`webview-ui/harness/live*.mjs`, throwaway `/tmp/svdemo` and
`/tmp/svdata`, isolated `/tmp/svprof` and sessions folder, $0.25 budget) in dark and light.

Open items and blockers:
- The approved mockup (`Plans/solvra-chat-redesign.html`) and `Plans/ui-test-harness/` are not in
  the repo (they're on the Mac checkout), and the claude.ai artifact isn't readable from the devbox.
  Tokens the brief didn't give are marked `reconcile` in `webview-ui/src/styles/tokens.css`.
- VS Code adds "Solvra: Focus on Chat View" and "View: Show Solvra" to the palette for every view;
  an extension can't hide them.
- The uncommitted 0.2.0 RC in the `vscode-release` worktree (another agent) overlaps these files.
  This stack is based on `origin/main` and doesn't include it.
- Changes made by `bash` are not checkpointed (file tools only).

---

# Limits and completion fix — 2026-10-10

Task worktree: `/home/cosmos/wt/github/Solvra/limits-validation`, branch `fix/limits-validation`, base `origin/main` at `a9f69d8`.

- Raised the default model request timeout from 600 to 1800 seconds; zero still disables it and explicit overrides remain honored.
- Confirmed main/subagent turn defaults already unlimited; existing regression exercises 52 turns and explicit caps.
- Removed the post-answer keyword browser gate and tool-input evidence heuristic. Added upfront task-relevant validation and honest result reporting guidance. This deliberately leaves validation selection to the model rather than claiming keyword matches prove correctness.
- Added streaming/nonstreaming regressions to ensure final output does not trigger another model request.
- Verification: `dotnet test Solvra.sln --nologo --verbosity minimal` passed 374/374 tests, including streaming/nonstreaming completion, 52-turn unlimited run, explicit turn caps, and timeout overrides. CLI help confirms 1800 seconds and unlimited turns. `git diff --check` passed. Existing CS1998 warnings remain.
- Deployment authorized 2026-10-11. Preparing local source commit and immutable release; retain previous release for rollback. No push requested. Explicit persisted timeout/turn settings continue to take precedence. No live provider calls were made.

---

# Solvra harness fixes — progress

## Configurable model request timeout — 2026-10-01

Worktree: `/home/cosmos/wt/github/Solvra/provider-timeouts`, branch `provider-timeouts`, based on
`origin/main` at `790ceb9`.

Confirmed that provider clients (except the newer ChatGPT provider) inherited .NET's implicit
100-second `HttpClient.Timeout`, independent of Solvra's run deadline. Added one provider-client
timeout setting with a 600-second default, `0` for no per-request cap, a one-day validation ceiling,
and propagation to Anthropic, OpenAI-compatible, Google, Ollama, Moonshot, and ChatGPT providers.
Configuration is available through `model_timeout_seconds`, `SOLVRA_MODEL_TIMEOUT_SECONDS`, and
`--model-timeout-seconds` on `run`/`chat`. The Harbor adapter passes `0`, leaving its explicit
task-specific `--time-limit-seconds` as the sole deadline.

Reproduced the DeepSeek failure against the deployed OpenCode Go gateway: HTTP 400 says DeepSeek
requires Global regions in workspace Privacy settings. This is an external account policy gate, so
the implementation documents the setting/alternate-provider remedy and deliberately does not retry
or silently substitute a model.

Verification: 367/367 .NET tests and 3/3 Harbor adapter tests pass. CLI help exposes both timeout
controls, and an invalid model timeout exits 1 with a clear validation error before making a request.
Final diff and whitespace review pass. The user authorized commit, push, and deployment after this
acceptance checkpoint; final release state is recorded in the task handoff. Workspace privacy and
secrets are unchanged.

## Lint screenshot integration — 2026-10-01

Worktree: `/home/cosmos/wt/github/Solvra/lint-integration`, branch `feature/lint-integration`, based
on `origin/main` at `fe99c81`.

Implemented the `SOLVRA-REQUESTS.md` contract: validated NDJSON inline PNG/JPEG/WebP input (four
images, 20 MiB each), native provider image blocks including Gemini, conservative model vision
metadata, rejection before turn start for text-only models, and image-file fallback for
PNG/JPEG/WebP/GIF. Added `--ask-all`, `--no-tools`, and `--ephemeral`; generation-only mode omits tool
definitions, hooks, reflection, skills, memory and instruction files. Protocol events now expose the
resolved model, usage, terminal status and error codes. Rich image messages round-trip in sessions.
Inline-image turns are always generation-only, preventing image prompt injection from producing tool calls;
an explicit follow-up text turn retains the normal tool-enabled workflow.

Verification so far: 361/361 .NET tests; visual probe correctly described by `gpt-5.6-luna` in one
tool-free turn; text-only `llama3.1` rejected with `unsupported_input` and no `start`; strict writes
and web fetches emitted permission before acting and denial prevented action; prompt-injection text
inside an image was treated as untrusted content with zero tool events. The verified change is ready
for an atomic production release; deployment state is recorded in the task handoff and release marker.

## Post-fix evaluation — 2026-10-01

Worktree: `/home/cosmos/wt/github/Solvra/postfix-eval`, branch `eval/postfix-20261001`, based on
and evaluating deployed `origin/main` revision `98a172d`.

Fresh results: 338/338 .NET tests, 2/2 Harbor adapter tests, four regression definitions valid, and
the no-op control failed 16/16 as required. `openai:glm-5.3-flash` passed 15/16 live scenarios; its
one failure returned a nested defaults dictionary by reference. `chatgpt:gpt-5.6-sol` passed all six
hard cases, including that failed cheap-model case. A live 15-second deadline probe interrupted active
`code_run`, exited 4 after 15.4 seconds, preserved 3,081 input/237 output tokens in JSON and audit
telemetry, and left no child process.

The eval runner had drifted from the production audit schema. It now recognizes current
`tool_call_completed` events as well as legacy `ToolExecution`, counts lifecycle actions once, supplies
an 840-second internal deadline under its 900-second process guard, and keeps empty TSV fields aligned.

Report: `reports/post-fix-evaluation-2026-10-01.md`. Raw artifacts:
`/data/solvra-evals/postfix-20261001/`. No production source, live configuration, deployment, service,
Docker image, or other worktree was changed by the evaluation.

## Deadline, telemetry, and verification regressions — 2026-10-01

Worktree: `/home/cosmos/wt/github/Solvra/deadline-verification`, branch
`fix/deadline-verification`, based on `main` at `aadbff5`.

Implemented a monotonic run deadline exposed as `--time-limit-seconds`. The parent loop, provider
calls, retry waits, tools, reflection, and subagents share one budget; bash and code-run timeouts
are capped by the remaining budget. Deadline completion has its own stop reason and exit code.
The loop gives a late-budget warning that asks for a saved result and targeted checks without
forbidding further edits.

Audit telemetry now writes model/tool started and completed lifecycle records with stable operation
IDs and writes provider-reported versus estimated usage after every completed model call. Deadline
cancellation writes an interrupted-operation record and returns all completed usage and conversation
state. Tool timing uses a distinct `tool_execution_metrics` event, eliminating the old ambiguous
double count. Lifecycle records omit tool inputs and outputs so cancellation evidence does not copy
credentials or command contents.

The default agent instructions now require explicit contracts, honest reporting of unavailable
validation, targeted diagnostics after repeated full-run failures, early performance measurement,
byte-scoped exact replacements, and browser-level checks for browser behavior. Regression metadata
and individually identified browser payloads live under `evals/regressions/`. The Harbor adapter
accepts an internal deadline and reconstructs partial token usage from audit logs when final JSON is
missing. Harbor does not expose its outer timeout through `AgentContext`; job configuration must set
`time_limit_seconds` to the task timeout minus a cleanup allowance.

An explicit browser/XSS task now gets one bounded verification-gate turn if the model tries to finish
without browser-automation evidence. The reminder requires Chromium/Selenium, Playwright, Puppeteer,
or equivalent execution and asks for an honest unverified result if no browser can run. It does not
gate ordinary HTML parsing or JavaScript work.

Verification: baseline 327/327 tests passed after restore; final suite is 338/338. Added coverage
includes monotonic expiry, 600-second requests capped to a subsecond remaining budget, process-tree
cancellation, partial usage and interruption telemetry, lifecycle redaction, exact replacement
preserving UTF-8 BOM/CRLF/all bytes outside the span, and the browser gate's extra turn. Harbor adapter
tests pass 2/2; regression metadata/browser corpus validation, Python compilation, shell syntax, CLI
help, self-contained publish, and `git diff --check` pass.

Controlled Terminal-Bench 2.1 runs with `chatgpt:gpt-5.6-sol` high reasoning:

- `sanitize-git-repo`: passed 1/1. The agent used byte-scoped replacement and exact-diff checks.
- `gpt2-codegolf`: finished normally before the deadline and switched to targeted checkpoint probes,
  but scored 0 because its output omitted the start of the expected continuation. It compiled, stayed
  under 5,000 bytes, and ran in 4.46 seconds in the verifier.
- `filter-js-from-html` before the gate: 12/28 browser batches raised alerts. After the gate, Solvra ran
  Chromium/Selenium with positive controls, found a malformed-quote bypass, and reduced the independent
  verifier failures to 3/28 batches. The score remained 0. The benchmark's separate clean-output check
  still reports 5/12 failures from comparing BeautifulSoup-normalized input with raw preserved bytes.
- `torch-tensor-parallelism`: no scored model attempt. Six Harbor starts and one direct isolated-image
  start failed at the first .NET TLS request with zero token usage. Simple calls sometimes succeeded in
  the same image later, so this remains an adapter/environment transport blocker. Static review confirms
  the corrected 2.1 prompt explicitly says row-parallel input is already sharded.

No push, deployment, or service restart occurred. Benchmark artifacts are under `/data/tb2/jobs/`
with job names `tb21-solvra-deadline-900-r1`, `tb21-solvra-html-r1`, and `tb21-solvra-html-r2`;
the tensor transport attempts use `tb21-solvra-tensor-r1` through `r5`.

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

## 2026-10-02 — Subagent and effort controls (feature/subagent-effort)

- Based on `origin/main` at `b25ee1d`; implementation is isolated in this worktree.
- Added strict `low|medium|high|xhigh` effort parsing (`max` remains compatible), subagent `auto|off`, child model/effort defaults, and per-child effort overrides.
- Effort is propagated to child sessions, audit/tracing, OpenAI reasoning payloads and ChatGPT Responses payloads. Disabled delegation is omitted from model tools and rejected if directly invoked.
- `dotnet test Solvra.sln`: 376 passed, 0 failed; the scripted loop completed 52 turns under the unlimited default. CLI help and invalid-value probes passed.
- VS Code extension `npm run compile`: passed; its max-turns default is also unlimited and explicit `0` is preserved.
- Browser gate: Playwright with Chromium 153 executed benign unlimited (`0`) and limited (`50`) inputs plus negative, malformed, and hostile-script cases; invalid inputs were rejected without changing rendered state and no script executed. This is supplemental because runtime turn handling remains in .NET.
- Removed the implicit 50-turn ceiling: `max_turns` now defaults to `0` (unlimited), while positive explicit limits retain the existing MaxTurns stop behavior. Subagents inherit the parent limit unless they explicitly request another value.
- The implementation was committed and pushed to draft PR #2. No deployment, service change, or paid model call performed.
