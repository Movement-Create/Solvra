# `solvra run --output-format stream-json`

A machine protocol for UIs such as the VS Code extension. Stdout carries one JSON event per
line, and nothing else. Stdin carries control messages, one JSON object per line. Because stdin
is the control channel, the prompt must be passed as an argument (`-` is rejected).

```sh
solvra run "Fix the bug in sample.js" --output-format stream-json \
  --permission-mode default --session <id> --title "Fix sample.js" \
  --allow-rule edit --allow-rule execute:node
```

Plain text output and `--json` output are unchanged. `--output-format json` is the same as `--json`.

## Events (stdout)

Every event has a `type`. Events that belong to an agent also have `agent`: either `"main"` or a
sub-agent id such as `"a1"`. Several sub-agents can run at the same time, and their events
interleave.

| type | fields |
|---|---|
| `session` | `id` (null with `--ephemeral`), `title`, `model`, `provider`, `cwd`, `mode` (ask, ask-all, plan, auto), `effort`, `resumed`, `rules` |
| `text_delta` | `agent`, `text` |
| `thinking_delta` | `agent`, `text`. Sent when the provider streams reasoning. |
| `tool_call` | `agent`, `id`, `name`, `input`, `summary` (e.g. "Edit sample.js"), `kind` (read, edit, execute, agent, network) |
| `permission_request` | `id` (p1, p2, …), `agent`, `tool_call_id`, `tool`, `kind`, `summary`, `diff` (unified diff or null), `command` (or null), `always` (what "don't ask again" would remember, e.g. `edit` or `execute:node`; null when it cannot be offered) |
| `permission_rule` | `rule`, `kind`, `prefix`. Sent after an `allow_always` answer. Pass the rule back with `--allow-rule` on later runs of the same session. |
| `tool_result` | `agent`, `id`, `ok`, `output` (capped at 20,000 characters), `diff` (for edits), `meta` `{added, removed, lines}` |
| `agent_start` | `agent`, `parent`, `tool_call_id`, `title` |
| `agent_end` | `agent`, `status` (ok, error, cancelled) |
| `plan` | `agent`, `items` `[{text, status: done, active or pending}]`. Comes from the `todo` tool of the main agent. |
| `usage` | `agent`, `input`, `output`, `cost_usd`: totals across the main agent and all sub-agents. `context_pct`: the main agent's last request as a share of the model's context window. |
| `error` | `message`, `fix` (`add_api_key`, `rate_limited`, `retry` or null), `provider` |
| `done` | `stop_reason` (text, max_turns, max_budget, deadline, error, cancelled), `text`, `error`, `session_id`, `turns`, `duration_ms`, `cost_usd`, `usage` `{input, output}` |

## Control messages (stdin)

```jsonc
{"type":"permission_response","id":"p1","decision":"allow|allow_always|deny","message":"optional text for the agent"}
{"type":"cancel"}
```

- **deny** refuses the tool. If you include a `message`, the agent receives it together with the
  refusal ("The user said: …") and continues from there.
- **allow_always** is remembered for the rest of the run, either for the whole kind (`edit`,
  `agent`, `network`, `read`) or, for commands, for the first word of a simple command
  (`execute:node`). Commands that contain shell operators (`;`, `&&`, `|`, redirects, `$()`,
  backticks, or `FOO=bar` prefixes) are never covered by a rule, so they always ask.
- **cancel** denies any open request, stops the run and ends with `done` and
  `stop_reason: "cancelled"` (exit code 130). The session file keeps the user/assistant pairs
  in order, so the session still resumes.
- If stdin closes (EOF), every open request and every later request is denied: with nobody left
  to answer, the run fails closed.

## Modes

`--permission-mode` pins the mode: `default` (ask before edits, commands and sub-agents),
`ask-all`, `plan` (read-only: write, execute and agent tools are refused and never ask) and
`auto` (nothing asks). Sub-agents inherit the mode and send their approvals through the same
channel, tagged with their own `agent` id.

## Sessions

- `--session <id>` resumes the conversation, or creates it if it does not exist.
- `--title` names a new session. Without it, the title is the first line of the prompt
  (whitespace collapsed, at most 60 characters).
- `solvra session list --json` returns `[{id, title, created_at, updated_at, model, provider}]`,
  newest activity first. A session without a stored title shows its first user message, the
  same rule the VS Code sidebar uses.
- `solvra session rename <id> <title>` appends a `session_title` event to the session file.
  Existing lines are never rewritten.
