# Terminal-Bench failure regressions

These fixtures separate deterministic harness guarantees from stochastic model outcomes.
They do not overwrite the historical Terminal-Bench 2.0 score.

- `manifest.json` records the four observed failures and their independent acceptance axes.
- `browser_cases.json` assigns one ID to every browser payload so a failure names the triggering input.
- `validate_fixtures.py` rejects incomplete or internally inconsistent fixture definitions.
- Core deadline, telemetry, and exact-byte behavior is exercised by `dotnet test`.
- Corrected task-level trials use Harbor dataset `terminal-bench/terminal-bench-2-1` and the adapter in `evals/harbor/`.

Run the deterministic gate:

```sh
python3 evals/regressions/validate_fixtures.py
dotnet test --no-restore
python3 -m py_compile evals/harbor/solvra_harbor_agent.py
```

Browser and PyTorch correctness remain task-level checks because the Solvra repository does not
vendor Chromium or PyTorch. The corrected benchmark containers supply those dependencies. Record
their dataset digest, model, reasoning effort, agent timeout, internal `time_limit_seconds`, and
trial count with every result.
