# Corrected benchmark review

Reviewed against `harbor-framework/terminal-bench-2-1` commit
`7131e4375048a0e408a8fb404b5f499d726b695b` on 2026-10-01.

The corrected dataset is the task-level integration gate, but it does not replace the independent
regressions in this repository:

- `gpt2-codegolf` keeps a 900-second agent timeout and a separate 90-second generated-program
  timeout. Source size, compilation, output correctness, development deadline, and executable
  performance must be reported independently.
- `filter-js-from-html` raises both agent and verifier timeouts to 1800 seconds and uses Selenium.
  Its verifier still concatenates inputs into batches and treats browser timeout as no alert, so it
  cannot identify an individual triggering input and is not a sufficient security regression by
  itself. `browser_cases.json` supplies stable per-input IDs; timeout must be an infrastructure
  failure in any maintained browser runner.
- `torch-tensor-parallelism` explicitly slices the row-parallel input before calling the layer. Its
  tests cover world sizes 1, 2, and 4, both bias settings, forward equality, weight gradients, and
  bias gradients. That makes the already-sharded row input an explicit regression contract.
- `sanitize-git-repo` still warrants two independent results: sensitive-value absence and exact
  bytes outside approved replacement spans. The harness unit test covers BOM, CRLF, absent final
  newline, and incidental quoting.

The source report remains the historical Terminal-Bench 2.0 result. New runs must record the 2.1
dataset identifier and commit/digest and must not rewrite the old score.
