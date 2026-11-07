# graph-memory tools

Task tooling for viewing Solvra runs and loading them into a graph. Solvra itself has no OTel
exporter and no graph writer; these scripts read its local telemetry files
(`~/.local/share/solvra/{logs,sessions}`) and never modify them.

| Script | Does |
|---|---|
| `solvra_spans_to_otlp.py` | re-emits the spans of one run (`logs/traces.jsonl`, by `trace_id`) as OTLP to a viewer such as Arize Phoenix. `--raw` keeps Solvra's untyped spans; `--openinference` adds OpenInference kinds and, with `--session-file`, full tool input/output. `--id-salt` lets a second copy of the same run coexist in a viewer that requires unique span ids. |
| `ingest_solvra_run.py` | loads one run into Neo4j through `neo4j-agent-memory`'s reasoning-trace API: `Conversation → Run → ReasoningTrace → ReasoningStep → ToolCall` (+ `Decision` for `decide` tool calls, none today). Full I/O, order and join keys are preserved; fields the telemetry lacks are written as null and listed. `--dry-run` only reports what it would load. |

Requirements: Python 3.12, `arize-phoenix`, `neo4j-agent-memory[opentelemetry]` (a venv at
`~/.local/opt/graph-memory/venv` on devbox). Neo4j credentials come from the environment
(`NEO4J_URI`, `NEO4J_USER`, `NEO4J_PASSWORD`; on devbox `set -a; . ~/env/graph-memory.env; set +a`).
Run with `python -I`. Operating notes, the stack setup and the tested Cypher queries live in
AIMastermind `ops/graph-memory-setup-notes.md`, `ops/graph-memory-queries.md`; the telemetry gaps
these scripts work around are in `ops/telemetry-audit.md` and `ops/solvra-telemetry-brief.md`.
