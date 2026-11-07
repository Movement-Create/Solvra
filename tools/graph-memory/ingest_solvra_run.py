#!/usr/bin/env python3
"""Load ONE Solvra run (one agent.session trace) into Neo4j through neo4j-agent-memory's reasoning-trace API.

Sources (all read-only):
  traces.jsonl            -> run boundary, span ids, latencies           (S1)
  logs/audit-*.jsonl      -> operation ids, provider token usage          (S2)
  sessions/<id>.jsonl     -> full prompt, full tool input/output, answer  (S3)
  sessions/cost-ledger    -> run usage/cost line                          (S4)
Graph:
  (:Conversation {session_id})-[:HAS_RUN]->(:Run {trace_id})-[:HAS_TRACE]->(:ReasoningTrace)
  (:ReasoningTrace)-[:HAS_STEP]->(:ReasoningStep)-[:USES_TOOL]->(:ToolCall)
  (:Message user)-[:TRIGGERED]->(:Run);  (:Run)-[:PRODUCED]->(:Message assistant)
  (:ReasoningStep)-[:DECIDED]->(:Decision)   only when a `decide` tool call exists (none in Solvra today)
Nothing is truncated. Fields the telemetry does not carry are written as null and listed at the end.
Credentials come from the environment (NEO4J_URI/NEO4J_USER/NEO4J_PASSWORD), never from arguments.
"""
import argparse, asyncio, hashlib, json, os, sys
from datetime import datetime, timezone, timedelta
from pathlib import Path
from pydantic import SecretStr
from neo4j import AsyncGraphDatabase
from neo4j_agent_memory import MemoryClient, MemorySettings
from neo4j_agent_memory.core.memory import ToolCallStatus

SOLVRA = Path.home() / ".local/share/solvra"
MISSING = ["agent_version / source_revision / instruction_revision / system_prompt (only when the run has a run_start record)", "cwd (not in run records; taken from tool_use.input.cwd)",
           "decision/judgment records (only when the run used the decide tool)", "evidence record ids (only for tool_results that carry records)", "verification pass/fail + attempt (only when the run used the verify tool)", "outcome/filed artifact (only from a run_end record)", "cost (always 0 for chatgpt provider)"]

def ts(s): 
    s = s.rstrip("Z"); head, _, frac = s.partition("."); 
    return datetime.fromisoformat(head).replace(tzinfo=timezone.utc) + timedelta(microseconds=int((frac + "000000")[:6]))
def text_of(content):
    if isinstance(content, str): return content
    return "\n".join(b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text")
def sha(s): return hashlib.sha256(s.encode()).hexdigest()
BAD = []
def jl(p):
    out = []
    for n, l in enumerate(open(p), 1):
        if not l.strip(): continue
        try: out.append(json.loads(l))
        except json.JSONDecodeError: BAD.append(f"{p}:{n}")
    return out

async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--trace-id", required=True)
    ap.add_argument("--solvra-dir", default=str(SOLVRA))
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args(); root = Path(a.solvra_dir)

    # S1: run boundary
    spans = [r for r in jl(root / "logs/traces.jsonl") if r["trace_id"] == a.trace_id]
    if not spans: sys.exit("trace not found")
    sess = next(r for r in spans if r["type"] == "span_start" and r["name"] == "agent.session")
    sess_end = next(r for r in spans if r["type"] == "span_end" and r["name"] == "agent.session")
    session_id = sess["attributes"]["session_id"]; t0, t1 = ts(sess["start_time"]), ts(sess_end["timestamp"])
    tool_spans = sorted([r for r in spans if r["type"] == "span_start" and r["name"] == "tool.execute"], key=lambda r: r["start_time"])
    tool_span_end = {r["span_id"]: r for r in spans if r["type"] == "span_end" and r["name"] == "tool.execute"}
    llm_spans = sorted([r for r in spans if r["type"] == "span_end" and r["name"] == "llm.call"], key=lambda r: r["start_time"])
    llm_resp = {r["span_id"]: r for r in spans if r["type"] == "span_event" and r["event_name"] == "llm.response"}

    # S2: audit window (file is named by process start date, so scan all)
    audit = [r for p in sorted((root / "logs").glob("audit-*.jsonl")) for r in jl(p)
             if r.get("session_id") == session_id and t0 - timedelta(seconds=2) <= ts(r["timestamp"].replace("+00:00", "")) <= t1 + timedelta(seconds=2)]
    model_calls = {r["data"]["turn"]: r["data"] for r in audit if r["event_type"] == "model_call_completed"}
    model_started = {r["data"]["turn"]: r["data"] for r in audit if r["event_type"] == "model_call_started"}
    tool_audit = {r["data"]["operation_id"]: r["data"] for r in audit if r["event_type"] == "tool_execution_metrics"}
    tool_turn = {r["data"]["operation_id"]: r["data"]["turn"] for r in audit if r["event_type"] == "tool_call_started"}

    # S3: session events inside the window
    sev = [e for e in jl(root / f"sessions/{session_id}.jsonl") if t0 - timedelta(seconds=3) <= ts(e["timestamp"]) <= t1 + timedelta(seconds=2)]
    config = jl(root / f"sessions/{session_id}.jsonl")[0]["data"]["config"]
    user_msgs = [e for e in sev if e["type"] == "user_message"]; finals = [e for e in sev if e["type"] == "assistant_message"]
    run_end = next((e["data"] for e in sev if e["type"] == "run_end"), None)  # written by telemetry package B; absent in older runs
    run_start = next((e["data"] for e in sev if e["type"] == "run_start"), None)  # telemetry package A; carries revision and instruction hashes
    all_events = jl(root / f"sessions/{session_id}.jsonl")
    system_prompt = next((e["data"] for e in reversed([e for e in all_events if e["type"] == "system_prompt" and ts(e["timestamp"]) <= t0 + timedelta(seconds=3)])), None)
    if len(user_msgs) != 1: sys.exit(f"expected one user_message in window, found {len(user_msgs)}")
    user_text = text_of(user_msgs[0]["data"]["content"]); final_text = text_of(finals[-1]["data"]["content"]) if finals else None
    turns = [e for e in sev if e["type"] == "assistant_turn"]
    results = {e["data"]["call_id"]: e["data"] for e in sev if e["type"] == "tool_result"}
    calls = [(ti + 1, b) for ti, e in enumerate(turns) for b in e["data"]["content"] if b.get("type") == "tool_use"]
    if len(calls) != len(tool_spans): sys.exit(f"{len(calls)} session tool calls vs {len(tool_spans)} tool spans: refusing to guess the join")
    # S4
    cost = next((r for r in jl(root / "sessions/cost-ledger.jsonl") if r["session_id"] == session_id and t0 <= ts(r["timestamp"]) <= t1 + timedelta(seconds=2)), None)
    cwd = next((b["input"].get("cwd") for _, b in calls if isinstance(b.get("input"), dict) and b["input"].get("cwd")), None)

    if BAD: print(f"skipped {len(BAD)} malformed line(s): {BAD[:5]}", file=sys.stderr)
    print(f"run {a.trace_id} session {session_id} {t0.isoformat()} -> {t1.isoformat()}: {len(turns)} assistant turns, {len(calls)} tool calls, {len(audit)} audit lines, cost line {'yes' if cost else 'no'}")
    if a.dry_run: return

    settings = MemorySettings(neo4j={"uri": os.environ["NEO4J_URI"], "username": os.environ["NEO4J_USER"], "password": SecretStr(os.environ["NEO4J_PASSWORD"])})
    driver = AsyncGraphDatabase.driver(os.environ["NEO4J_URI"], auth=(os.environ["NEO4J_USER"], os.environ["NEO4J_PASSWORD"]))
    async with MemoryClient(settings) as client:
        async with driver.session() as s0:
            n = (await (await s0.run("MATCH (r:Run {trace_id: $t}) RETURN count(r) AS n", t=a.trace_id)).single())["n"]
        if n: sys.exit("run already loaded; delete it first (see queries.md)")
        st = client.short_term; rm = client.reasoning
        kw = dict(generate_embedding=False, extract_entities=False, extract_relations=False, extraction_mode="skip")
        umsg = await st.add_message(session_id, "user", user_text, metadata={"solvra_timestamp": user_msgs[0]["timestamp"], "source": "sessions/<id>.jsonl user_message"}, **kw)
        trace = await rm.start_trace(session_id, task=user_text, generate_embedding=False, triggered_by_message_id=umsg.id,
                                     metadata={"solvra_trace_id": a.trace_id, "root_span_id": sess["span_id"], "model": sess["attributes"].get("model"), "provider": sess["attributes"].get("provider")})
        step_ids, decisions, pos = [], [], 0
        for ti, e in enumerate(turns, start=1):
            blocks = e["data"]["content"]; mc = model_calls.get(ti, {}); ms = model_started.get(ti, {})
            llm = llm_spans[ti - 1] if ti - 1 < len(llm_spans) else {}
            step = await rm.add_step(trace.id, thought=text_of(blocks) or None, action=", ".join(b["name"] for b in blocks if b.get("type") == "tool_use") or None,
                                     generate_embedding=False, metadata={"turn": ti, "solvra_timestamp": e["timestamp"], "llm_operation_id": mc.get("operation_id"), "llm_span_id": llm.get("span_id"),
                                                                         "llm_duration_ms": llm.get("duration_ms"), "estimated_input_tokens": ms.get("estimated_input_tokens"),
                                                                         "provider_input_tokens": (mc.get("usage") or {}).get("input"), "provider_output_tokens": (mc.get("usage") or {}).get("output"),
                                                                         "stop_reason": mc.get("stop_reason"), "effort": ms.get("effort"), "reasoning_blocks": sum(1 for b in blocks if b.get("type") == "reasoning")})
            step_ids.append(step.id)
            for b in blocks:
                if b.get("type") != "tool_use": continue
                pos += 1; span = tool_spans[pos - 1]; res = results.get(b["id"]); out = (res or {}).get("output"); err = bool((res or {}).get("is_error"))
                tc = await rm.record_tool_call(step.id, b["name"], b["input"], result=out, status=ToolCallStatus.ERROR if err else ToolCallStatus.SUCCESS,
                                               duration_ms=tool_span_end.get(span["span_id"], {}).get("duration_ms"), error=(out if err else None))
                async with driver.session() as s:
                    await s.run("MATCH (tc:ToolCall {id: $id}) SET tc.call_id=$cid, tc.position=$pos, tc.turn=$turn, tc.span_id=$span, tc.solvra_trace_id=$t, "
                                "tc.output_bytes=$ob, tc.output_sha256=$osha, tc.input_sha256=$isha, tc.audit_duration_ms=$ad, tc.record_ids=$records, tc.record_count=$rcount, tc.truncated=$trunc, tc.spill_path=$spill, tc.exit_code=$exit",
                                id=str(tc.id), cid=b["id"], pos=pos, turn=tool_turn.get(b["id"], ti), span=span["span_id"], t=a.trace_id,
                                ob=len(out) if out is not None else None, osha=sha(out) if out is not None else None, isha=sha(json.dumps(b["input"], sort_keys=True)),
                                ad=tool_audit.get(b["id"], {}).get("duration_ms"),
                                records=json.dumps((res or {}).get('records')) if (res or {}).get('records') else None, rcount=len((res or {}).get('records') or []) if res and 'records' in res else None,
                                trunc=(res or {}).get('truncated'), spill=(res or {}).get('spill_path'), exit=(res or {}).get('exit_code'))
                for rec in (res or {}).get('records') or []:
                    key = rec.get('path') or rec.get('url') or rec.get('command_sha256') or rec.get('code_sha256')
                    if not key: continue
                    async with driver.session() as s:
                        await s.run("MATCH (tc:ToolCall {id: $id}) MERGE (e:Record {kind: $kind, key: $key}) SET e += $props MERGE (tc)-[:CITES {line: $line}]->(e)",
                                    id=str(tc.id), kind=rec.get('kind', 'record'), key=key, line=rec.get('line'),
                                    props={k: v for k, v in rec.items() if k not in ('kind', 'line') and isinstance(v, (str, int, float, bool))})
                if b["name"] == "decide" and not err:  # judgment point recorded by telemetry package B's decide tool
                    decisions.append((step.id, b["id"], b["input"], out))
                if b["name"] == "verify" and not err:
                    async with driver.session() as s:
                        await s.run("MATCH (tc:ToolCall {id: $id}) SET tc.is_verification=true", id=str(tc.id))
        if finals and len(llm_spans) == len(turns) + 1:  # the answer turn is a model call too; keep its metrics as the last step
            ti = len(turns) + 1; mc = model_calls.get(ti, {}); ms = model_started.get(ti, {}); llm = llm_spans[-1]
            step_ids.append((await rm.add_step(trace.id, thought=None, action="final_answer", observation=None, generate_embedding=False,
                metadata={"turn": ti, "solvra_timestamp": finals[-1]["timestamp"], "llm_operation_id": mc.get("operation_id"), "llm_span_id": llm.get("span_id"), "llm_duration_ms": llm.get("duration_ms"),
                          "estimated_input_tokens": ms.get("estimated_input_tokens"), "provider_input_tokens": (mc.get("usage") or {}).get("input"), "provider_output_tokens": (mc.get("usage") or {}).get("output"),
                          "stop_reason": mc.get("stop_reason"), "effort": ms.get("effort"), "reasoning_blocks": 0})).id)
        final = await st.add_message(session_id, "assistant", final_text or "", metadata={"solvra_timestamp": finals[-1]["timestamp"] if finals else None, "source": "sessions/<id>.jsonl assistant_message"}, **kw) if final_text is not None else None
        await rm.complete_trace(trace.id, outcome=final_text, success=None if final_text is None else True)  # success=True means "answered"; no verification record exists
        async with driver.session() as s:
            await s.run("""
                MATCH (rt:ReasoningTrace {id: $tid}) MATCH (c:Conversation {session_id: $sid})
                MERGE (r:Run {trace_id: $t}) SET r.session_id=$sid, r.agent='solvra', r.agent_version=$version, r.source_revision=$source_revision, r.instruction_revision=$instruction_revision,
                  r.instruction_files=$instruction_files, r.system_prompt_sha256=$system_prompt_sha256, r.system_prompt=$system_prompt, r.tools_sha256=$tools_sha256, r.run_start_present=$run_start_present, r.model=$model, r.provider=$provider, r.cwd=$cwd, r.permission_mode=$pm, r.effort=$effort,
                  r.started_at=datetime($t0), r.ended_at=datetime($t1), r.duration_ms=$dur, r.turns=$turns, r.tool_calls=$ncalls,
                  r.input_tokens=$itok, r.output_tokens=$otok, r.cost_usd=$cost, r.cost_known=false, r.stop_reason=$stop,
                  r.verification=$verification, r.verification_count=$vcount, r.decisions=$decisions, r.outcome_filed=$artifacts, r.artifact_count=$acount,
                  r.run_end_present=$run_end_present, r.sources=$sources
                MERGE (c)-[:HAS_RUN]->(r) MERGE (r)-[:HAS_TRACE]->(rt)
                WITH r MATCH (u:Message {id: $umid}) MERGE (u)-[:TRIGGERED]->(r)
                WITH r OPTIONAL MATCH (f:Message {id: $fmid}) FOREACH (_ IN CASE WHEN f IS NULL THEN [] ELSE [1] END | MERGE (r)-[:PRODUCED]->(f))
                """, tid=str(trace.id), sid=session_id, t=a.trace_id, model=sess["attributes"].get("model"), provider=sess["attributes"].get("provider"), cwd=cwd,
                pm=config.get("permission_mode"), effort=config.get("effort"), t0=t0.isoformat(), t1=t1.isoformat(), dur=sess_end["duration_ms"], turns=len(llm_spans), ncalls=len(calls),
                itok=(cost or {}).get("input_tokens"), otok=(cost or {}).get("output_tokens"), cost=(cost or {}).get("cost_usd"), stop=model_calls.get(len(llm_spans), {}).get("stop_reason"),
                sources=[f"traces.jsonl trace_id={a.trace_id}", f"sessions/{session_id}.jsonl", "logs/audit-*.jsonl", "sessions/cost-ledger.jsonl"],
                umid=str(umsg.id), fmid=str(final.id) if final else "",
                verification=json.dumps(run_end["verifications"]) if run_end and run_end.get("verifications") else None,
                vcount=len(run_end.get("verifications") or []) if run_end else None,
                decisions=run_end.get("decisions") if run_end else None,
                artifacts=json.dumps(run_end["artifacts"]) if run_end and run_end.get("artifacts") else None,
                acount=len(run_end.get("artifacts") or []) if run_end else None,
                run_end_present=run_end is not None,
                version=(run_start or {}).get('version'), source_revision=(run_start or {}).get('source_revision'),
                instruction_revision=json.dumps([{'path': f['path'], 'git_blob': f.get('git_blob'), 'sha256': f.get('sha256')} for f in (run_start or {}).get('instruction_files') or []]) if run_start else None,
                instruction_files=[f['path'] for f in (run_start or {}).get('instruction_files') or []],
                system_prompt_sha256=(run_start or {}).get('system_prompt_sha256'), system_prompt=(system_prompt or {}).get('text'), tools_sha256=(run_start or {}).get('tools_sha256'),
                run_start_present=run_start is not None)
            for sid_, cid, inp, out in decisions:
                await s.run("MATCH (s:ReasoningStep {id: $sid}) CREATE (d:Decision {call_id: $cid, question: $q, answer: $ans, confidence: $conf, raw_input: $raw, raw_output: $out}) MERGE (s)-[:DECIDED]->(d)",
                            sid=str(sid_), cid=cid, q=inp.get("question"), ans=inp.get("choice", inp.get("answer")), conf=inp.get("confidence"), raw=json.dumps(inp), out=out)
            counts = await (await s.run("MATCH (r:Run {trace_id:$t})-[:HAS_TRACE]->(rt)-[:HAS_STEP]->(st) OPTIONAL MATCH (st)-[:USES_TOOL]->(tc) OPTIONAL MATCH (st)-[:DECIDED]->(d) RETURN count(DISTINCT st) AS steps, count(DISTINCT tc) AS tool_calls, count(DISTINCT d) AS decisions", t=a.trace_id)).single()
    await driver.close()
    print(f"loaded: steps={counts['steps']} tool_calls={counts['tool_calls']} decisions={counts['decisions']} (decisions are expected 0: no decide tool)")
    print("fields left null because the telemetry does not carry them:"); [print("  -", m) for m in MISSING]

asyncio.run(main())
