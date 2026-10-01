# Solvra post-fix evaluation — 2026-10-01

## Executive assessment

Solvra's deployed coding harness is healthy at revision `98a172d5d86ae7d96e0cd340c31848f030f4a93f`.
The deterministic suite passes 338/338, the maintained Harbor adapter tests pass 2/2, the cheap
`openai:glm-5.3-flash` model passed 15/16 end-to-end scenarios, and
`chatgpt:gpt-5.6-sol` passed all six hard scenarios. A live forced-deadline run stopped an active tool
at the configured deadline, retained provider usage, emitted interruption telemetry, and left no child
process behind.

The remaining weaknesses are mostly task/model quality rather than new core harness failures. The cheap
model produced an aliased nested-default dictionary in one debugging task and needed 408 seconds, 33
turns, and 558,799 input tokens for the Node cart task. Earlier Terminal-Bench 2.1 evidence still leaves
GPT-2 output correctness and three browser-sanitizer alert batches unresolved. The tensor-parallelism
task remains unscored because its isolated environment failed during .NET TLS setup before a model call.

This evaluation found and corrected one issue in the repository's evaluation runner: it still searched for the
retired `ToolExecution` audit event. Current Solvra emits `tool_call_completed` and a separate
`tool_execution_metrics` record. The runner now understands both formats, counts each
tool call once, and passes an 840-second internal deadline below its 900-second outer guard.

## Target and method

- Source and deployed revision: `98a172d5d86ae7d96e0cd340c31848f030f4a93f`.
- Active release: `/home/cosmos/.local/opt/solvra/releases/98a172d5d86a-20261001T104529Z`.
- Date: 2026-10-01 UTC.
- Scenario isolation: a fresh Git fixture and separate `SOLVRA_HOME` for every case.
- Scoring: hidden checks unavailable to the model; a no-op control had to fail every case.
- Cheap pass: all 16 maintained scenarios on `openai:glm-5.3-flash`, concurrency two.
- Strong pass: cases 01, 02, 03, 05, 10, and 13 on `chatgpt:gpt-5.6-sol`, concurrency two.
- Limits: 40 turns, 840-second Solvra deadline, and 900-second outer process guard.
- Raw artifacts: `/data/solvra-evals/postfix-20261001/`.

This is one attempt per model and case. It measures the observed build and model runs; it is not a
statistical estimate of pass probability.

## Results

| Layer | Result | Evidence |
|---|---:|---|
| .NET unit/integration tests | 338/338 pass | Includes deadline, process cleanup, telemetry, exact-edit, and browser-gate coverage |
| Harbor adapter tests | 2/2 pass | Complete-result parsing and partial usage recovery from audit logs |
| Regression metadata | 4/4 valid | Includes the individually identified browser corpus |
| No-op control | 0/16 pass | Confirms the hidden checks are not already satisfied |
| Cheap live suite | 15/16 pass | One model solution error; no provider or deadline failure |
| Strong hard subset | 6/6 pass | Includes the cheap model's failed traceback/config task |
| Live deadline probe | Pass | Exit 4 at 15.4 s, partial usage retained, child process removed |
| Deployed service | Active | Active release matches evaluated revision |

### Scenario detail

| # | Scenario | GLM flash | Seconds / turns / input | GPT-5.6-sol | Seconds / turns / input |
|---:|---|---:|---:|---:|---:|
| 01 | Python bug repair | Pass | 59 / 8 / 39,723 | Pass | 65 / 11 / 41,992 |
| 02 | Node cart bugs | Pass | 408 / 33 / 558,799 | Pass | 111 / 15 / 63,480 |
| 03 | Feature from specification | Pass | 67 / 6 / 30,629 | Pass | 114 / 12 / 47,973 |
| 04 | Cross-file rename | Pass | 20 / 6 / 24,115 | — | — |
| 05 | Traceback and nested config | **Fail** | 34 / 8 / 35,009 | Pass | 95 / 13 / 55,968 |
| 06 | 18 MB log analysis | Pass | 12 / 7 / 25,346 | — | — |
| 07 | Code-run computation | Pass | 14 / 4 / 13,763 | — | — |
| 08 | Web fetch and SSRF refusal | Pass | 16 / 6 / 25,264 | — | — |
| 09 | CSV, XLSX, and PDF generation | Pass | 21 / 9 / 38,391 | — | — |
| 10 | Subagent plus todo tracking | Pass | 59 / 7 / 28,025 | Pass | 192 / 15 / 53,249 |
| 11 | Session and memory continuity | Pass | 22 / 8 / 25,691 | — | — |
| 12 | Background server lifecycle | Pass | 33 / 9 / 35,262 | — | — |
| 13 | C# bug repair | Pass | 39 / 8 / 36,351 | Pass | 84 / 12 / 45,371 |
| 14 | Headless permission refusal | Pass | 9 / 3 / 9,290 | — | — |
| 15 | Read-only plan mode | Pass | 11 / 5 / 16,426 | — | — |
| 16 | Web search | Pass | 8 / 4 / 13,816 | — | — |

The cheap suite used 955,900 input tokens across 131 turns and 832 summed task-seconds. Because two
cases ran concurrently, summed task-seconds are not wall-clock runtime. The strong subset used 308,033
input tokens across 78 turns and 661 summed task-seconds.

## What the failure means

The cheap model's case 05 implementation recursively merged an override into defaults, but for a key
absent from the override it returned the original nested default value directly. The returned
configuration therefore still shared `DEFAULTS["tax"]`; mutation of a loaded configuration changed the
global defaults. Its own tests passed because they did not mutate the returned nested object. The hidden
test did, and correctly failed the solution.

This was not a Solvra crash, timeout, provider error, or scoring ambiguity. The run exited normally in
eight turns. GPT-5.6-sol passed the same hidden contract, showing the harness and fixture can support a
correct result. The useful engineering lesson is to preserve hidden aliasing checks and to prefer a
stronger model or escalation policy when a cheap model makes broad correctness claims without testing
object independence.

## Deadline and telemetry behavior

The live deadline probe asked GLM flash to invoke `code_run` with Python sleeping for 120 seconds and a
requested 600,000 ms tool timeout. Solvra's run deadline was 15 seconds.

- The model completed one call and reported 3,081 input and 237 output tokens.
- `tool_call_started` identified `code_run` as the active operation.
- The shared deadline cancelled it; there was no `tool_call_completed` because the operation was
  interrupted.
- `operation_interrupted` recorded reason `deadline`, the active operation type, and the completed usage.
- JSON output reported `stop_reason: deadline`, one turn, and the same usage.
- The process exited with code 4 after 15.4 seconds, inside the 30-second external guard.
- A process scan found no surviving `time.sleep(120)` child.

The full cheap and strong suites recorded 232 completed model calls with no `model_call_failed` event.
Tool actions were counted from `tool_call_completed`; the separate timing record was not counted as a
second action. Permission refusals and intentional negative tests still appear as tool errors, so raw
tool-error totals are not failure totals.

## Architecture status after the fixes

The existing CLI, composition, provider, agent-loop, tool registry, permissions, state, and server layers
remain intact. The current build adds a run-scoped control plane across those layers:

1. The CLI accepts `--time-limit-seconds` and creates one monotonic run deadline.
2. The agent loop shares that deadline with provider calls, retries, tools, reflection, and subagents.
3. Bash and code-run cap requested timeouts to the remaining run budget.
4. A run process tracker owns foreground and background children and cleans them up on completion or
   cancellation.
5. Incremental audit events record model and tool lifecycle transitions with operation IDs, completed
   usage, and interruption state without logging tool inputs or outputs.
6. The Harbor adapter passes an internal deadline and can reconstruct partial usage when cancellation
   prevents final JSON from being written.
7. The system prompt and browser gate add verification guidance: explicit input contracts, targeted
   diagnostics after repeated full runs, early performance checks, byte-scoped replacements, and
   browser execution for browser-security behavior.

The live probe confirms that these pieces operate as one path rather than only passing isolated unit
tests.

## Current strengths

- Deadline propagation, timeout capping, partial usage, and process cleanup work in a live provider run.
- The full tool surface remains functional: all 16 built-in tools were exercised in the cheap suite.
- Large-input handling is efficient: the 18 MB log task passed in 12 seconds and seven turns.
- Permission refusal and plan mode pass under headless execution.
- Session continuation, persistent memory, background processes, subagents, and generated documents all
  pass hidden checks.
- GPT-5.6-sol remains reliable on the six difficult coding scenarios and is substantially more efficient
  than GLM flash on the difficult Node task.

## Remaining risks and recommended work

1. **Keep model escalation based on evidence.** GLM flash is effective at 15/16, but its case 02 cost and
   case 05 aliasing miss show why a cheap-model result should be escalated when diagnostics repeat, token
   growth is steep, or a contract cannot be independently exercised.
2. **Finish the browser sanitizer.** The last controlled Terminal-Bench 2.1 run reduced alerting batches
   from 12/28 to 3/28 but still failed. Preserve individual-payload browser tests and fix the remaining
   parser/execution cases before claiming browser-safe sanitization.
3. **Finish GPT-2 correctness.** Deadline management and runtime improved: the program completed before
   the task deadline, compiled under 5 KB, and ran in 4.46 seconds, but generated the wrong continuation.
   Add intermediate-logit or reference-token probes rather than more full end-to-end attempts.
4. **Re-run tensor parallelism only after the image transport is stable.** The corrected task contract is
   explicit, but the prior attempt spent zero model tokens because .NET TLS setup failed in the isolated
   image. Treat that as infrastructure until a model call begins.
5. **Retain the eval-runner compatibility coverage.** Tool-dependent cases must follow telemetry schema
   changes and count lifecycle completion separately from timing metrics.
6. **Still-open product features:** model-generated context summaries, persistent shell working directory,
   edit undo/checkpoints, and the gateway format required by grok-4.6 remain outside this fix.

## Artifact index

- Cheap results: `/data/solvra-evals/postfix-20261001/glm-full/results.md`
- Strong results: `/data/solvra-evals/postfix-20261001/sol-hard/results.md`
- No-op control: `/data/solvra-evals/postfix-20261001/noop-control/results.md`
- Deadline probe: `/data/solvra-evals/postfix-20261001/live-deadline-2/`
- Evaluation runner change: `evals/run.sh`
- Historical TB2.1 artifacts: `/data/tb2/jobs/`
