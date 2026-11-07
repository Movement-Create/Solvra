#!/usr/bin/env python3
"""Re-emit Solvra's file-based spans (logs/traces.jsonl) as OTLP so a viewer like Phoenix can show them.

Solvra has no exporter; this is the "copy of the exporter" for the viewing stack. Two modes:
  --raw           faithful copy: same span names/attributes/ids/times, no OpenInference kinds (default)
  --openinference additionally sets openinference.span.kind (AGENT/CHAIN/LLM/TOOL) and, when the
                  matching session file is given, attaches full tool input/output and LLM output text.
Ids are preserved: Solvra's 64-bit hex trace_id is zero-extended to OTel's 128-bit trace id.
Nothing is truncated here; sizes are reported on stderr instead.
"""
import argparse, json, sys
from pathlib import Path
from datetime import datetime, timezone
from opentelemetry.sdk.trace import ReadableSpan, Event
from opentelemetry.sdk.resources import Resource
from opentelemetry.trace import SpanContext, SpanKind, TraceFlags, Status, StatusCode
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter

KIND = {"agent.session": "AGENT", "agent.turn": "CHAIN", "llm.call": "LLM", "tool.execute": "TOOL"}

def ns(ts: str) -> int:
    ts = ts.rstrip("Z")
    head, _, frac = ts.partition(".")
    frac = (frac + "000000000")[:9]
    dt = datetime.fromisoformat(head).replace(tzinfo=timezone.utc)
    return int(dt.timestamp()) * 1_000_000_000 + int(frac)

def flat(d):
    out = {}
    for k, v in (d or {}).items():
        if v is None: continue
        out[k] = v if isinstance(v, (str, int, float, bool)) else json.dumps(v, ensure_ascii=False)
    return out

def load_session_io(path: Path):
    """call_id -> (tool_use dict, tool_result dict) and ordered assistant texts from a Solvra session file."""
    calls, results, turns = {}, {}, []
    for line in path.read_text().splitlines():
        if not line.strip(): continue
        ev = json.loads(line)
        if ev["type"] == "assistant_turn":
            turns.append(ev)
            for b in ev["data"]["content"]:
                if b.get("type") == "tool_use": calls[b["id"]] = (ev["timestamp"], b)
        elif ev["type"] == "tool_result":
            results[ev["data"]["call_id"]] = ev
        elif ev["type"] == "assistant_message":
            turns.append(ev)
    return calls, results, turns

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--traces", default=str(Path.home() / ".local/share/solvra/logs/traces.jsonl"))
    ap.add_argument("--trace-id", required=True)
    ap.add_argument("--session-file", help="Solvra sessions/<id>.jsonl; enables full I/O in --openinference mode")
    ap.add_argument("--endpoint", default="http://127.0.0.1:6006/v1/traces")
    ap.add_argument("--project", default="solvra-raw")
    ap.add_argument("--openinference", action="store_true")
    ap.add_argument("--id-salt", default="0", help="hex XOR applied to trace/span ids so a second copy of the same run can coexist in the viewer (ids are unique per Phoenix DB); original ids stay in solvra.* attributes")
    a = ap.parse_args()

    recs = [json.loads(l) for l in open(a.traces) if l.strip()]
    recs = [r for r in recs if r["trace_id"] == a.trace_id]
    if not recs: sys.exit(f"no records for trace {a.trace_id}")
    starts = {r["span_id"]: r for r in recs if r["type"] == "span_start"}
    ends = {r["span_id"]: r for r in recs if r["type"] == "span_end"}
    events = {}
    for r in recs:
        if r["type"] == "span_event": events.setdefault(r["span_id"], []).append(r)

    calls = results = {}; turns = []
    if a.openinference and a.session_file:
        calls, results, turns = load_session_io(Path(a.session_file))
    # order tool.execute spans so they can be matched to session tool_use blocks by position
    tool_spans = sorted([s for s in starts.values() if s["name"] == "tool.execute"], key=lambda s: s["start_time"])
    call_ids = sorted(calls, key=lambda c: calls[c][0])  # stable: keeps content[] order within one assistant_turn
    # keep only calls inside this trace's time window
    t0, t1 = min(ns(s["start_time"]) for s in starts.values()), max(ns(e["timestamp"]) for e in ends.values())
    call_ids = [c for c in call_ids if t0 - 2_000_000_000 <= ns(calls[c][0]) <= t1]
    span_to_call = {s["span_id"]: s["attributes"]["operation_id"] for s in tool_spans if s.get("attributes", {}).get("operation_id") in calls}
    if not span_to_call:  # older runs: spans carry no operation_id, join by order
        span_to_call = dict(zip([s["span_id"] for s in tool_spans], call_ids)) if len(call_ids) == len(tool_spans) else {}
    if a.openinference and a.session_file and not span_to_call:
        print(f"warn: {len(tool_spans)} tool spans vs {len(call_ids)} session tool calls; I/O not attached", file=sys.stderr)

    resource = Resource.create({"service.name": "solvra", "openinference.project.name": a.project,
                                "solvra.trace_source": a.traces})
    salt = int(a.id_salt, 16)
    tid = int(a.trace_id, 16) ^ (salt << 64)
    sx = lambda h: int(h, 16) ^ salt
    out, sizes = [], []
    for sid, s in starts.items():
        e = ends.get(sid)
        attrs = flat(s.get("attributes"))
        attrs["solvra.span_id"] = sid
        attrs["solvra.trace_id"] = a.trace_id
        evs = [Event(r["event_name"], flat(r.get("event_attributes")), ns(r["timestamp"])) for r in events.get(sid, [])]
        status = Status(StatusCode.UNSET)
        if a.openinference:
            attrs["openinference.span.kind"] = KIND.get(s["name"], "UNKNOWN")
            if s["name"] == "llm.call":
                attrs["llm.model_name"] = s["attributes"].get("model", "")
                attrs["llm.token_count.prompt"] = s["attributes"].get("input_tokens", 0)
                for r in events.get(sid, []):
                    if r["event_name"] == "llm.response":
                        attrs["llm.token_count.completion"] = r["event_attributes"].get("output_tokens", 0)
            if s["name"] == "tool.execute":
                attrs["tool.name"] = s["attributes"].get("tool", "")
                cid = span_to_call.get(sid)
                if cid:
                    use = calls[cid][1]; res = results.get(cid)
                    attrs["tool.call_id"] = cid
                    attrs["input.value"] = json.dumps(use["input"], ensure_ascii=False)
                    attrs["input.mime_type"] = "application/json"
                    if res:
                        attrs["output.value"] = res["data"]["output"]
                        attrs["output.mime_type"] = "text/plain"
                        sizes.append((cid, len(res["data"]["output"])))
                        if res["data"].get("is_error"): status = Status(StatusCode.ERROR)
        for r in events.get(sid, []):
            if r["event_name"] == "tool.result" and r["event_attributes"].get("is_error"):
                status = Status(StatusCode.ERROR)
        ctx = SpanContext(tid, sx(sid), is_remote=False, trace_flags=TraceFlags(TraceFlags.SAMPLED))
        parent = SpanContext(tid, sx(s["parent_span_id"]), is_remote=False, trace_flags=TraceFlags(TraceFlags.SAMPLED)) if s.get("parent_span_id") else None
        out.append(ReadableSpan(name=s["name"], context=ctx, parent=parent, resource=resource, attributes=attrs,
                                events=evs, kind=SpanKind.INTERNAL, status=status,
                                start_time=ns(s["start_time"]), end_time=ns(e["timestamp"]) if e else None))
    res = OTLPSpanExporter(endpoint=a.endpoint).export(out)
    print(f"exported {len(out)} spans for trace {a.trace_id} to {a.endpoint} project={a.project}: {res.name}")
    for cid, n in sizes: print(f"  full output attached call_id={cid} bytes={n}", file=sys.stderr)

if __name__ == "__main__":
    main()
