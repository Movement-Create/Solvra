# Solvra scenario evals

End-to-end tasks that run the real `solvra` binary against a live model and verify the result with
hidden checks the agent never sees. They exercise real bug fixing, writing code from a spec,
refactoring, debugging, large inputs and every built-in tool.

```bash
evals/run.sh                                   # all cases, chatgpt:gpt-5.6-luna, 3 in parallel
evals/run.sh -m chatgpt:gpt-5.6-sol 01 05 13   # selected cases, another model
evals/run.sh -m openai:glm-5.3 -j 1            # any provider:model Solvra supports
SOLVRA_BIN=/bin/true evals/run.sh              # dry run: every case must FAIL (checks are not trivially satisfied)
```

Results go to `/tmp/solvra-evals/<timestamp>-<model>/` (`results.md`, and per case `out.txt`, `err.txt`,
`check.txt`, the agent's `work/` repo and its isolated `solvra-home/` with sessions, audit log and memory).
Each case runs with its own `SOLVRA_HOME`, so evals never touch your real Solvra memory or sessions.

| Case | What it proves |
|---|---|
| 01-py-real-bugs | Three real Python bugs (tokenising, even-length median, case-insensitive ranking) + regression tests; hidden edge-case tests |
| 02-node-cart-bug | Node.js root-cause fixes (qty merge, double discount, cent rounding/formatting) with `node --test` |
| 03-feature-from-spec | Writes a new module from a spec (duration parser/formatter) + its own tests; hidden spec tests |
| 04-refactor-rename | Cross-file rename with grep/glob/file_edit; no old names left; tests still pass |
| 05-traceback-debug | Root cause from a stack trace across modules (nested config merge without mutating defaults) |
| 06-large-log | 18 MB log: finds all error codes without blowing the context window |
| 07-code-run | Uses the `code_run` tool for computation |
| 08-web-fetch | `web_fetch` of real pages, and the SSRF guard refusing a local address |
| 09-docs-csv-xlsx | `csv_write`, `spreadsheet_create`, `doc_create` produce correct CSV/XLSX/PDF |
| 10-subagent-todo | Delegates work to a subagent (`agent`) while fixing a bug itself; tracks both with `todo` |
| 11-memory-session | `--session` continuation across runs and `memory_note`/`memory_recall` across sessions |
| 12-background-server | Starts a server with background bash, queries it, stops it cleanly |
| 13-csharp-bug | .NET 8 bugs (overdraft logic, interest rounding, input validation) with hidden xunit tests |
| 14-headless-permissions | Default mode with no approver refuses destructive writes and reports it |
| 15-plan-mode | `--plan` changes nothing and describes the fix |
| 16-web-search | `web_search` finds real sites |

Adding a case: create `cases/NN-name/` with `repo/`, `prompt.txt` (or `steps.txt` with
`FLAGS ::: PROMPT` lines), and `check.sh`. In `check.sh`, call `fail "reason"` for each problem; helpers
`tool_used NAME` / `tool_errored NAME` read the audit log; `$WORK`, `$HIDDEN`, `$OUT`, `$SOLVRA_HOME` are set.
Put hidden tests under `$HIDDEN`, never in `repo/`. Always dry-run a new case with `SOLVRA_BIN=/bin/true`.
