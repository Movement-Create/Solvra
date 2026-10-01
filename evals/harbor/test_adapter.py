from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from harbor.models.agent.context import AgentContext

from solvra_harbor_agent import Solvra


class AdapterTelemetryTests(unittest.TestCase):
    def test_partial_audit_usage_survives_missing_final_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            audit_dir = root / "solvra-home" / "logs"
            audit_dir.mkdir(parents=True)
            events = [
                {"event_type": "model_call_completed", "data": {"usage": {"input": 10, "output": 3}}},
                {"event_type": "model_call_completed", "data": {"usage": {"input": 20, "output": 4}}},
                {"event_type": "model_call_failed", "data": {"error_type": "HttpRequestException"}},
                {"event_type": "operation_interrupted", "data": {"reason": "deadline"}},
                "truncated-json-is-ignored",
            ]
            (audit_dir / "audit-2026-10-01.jsonl").write_text(
                "\n".join(json.dumps(event) if isinstance(event, dict) else event for event in events)
            )
            agent = Solvra(logs_dir=root, model_name="chatgpt/gpt-5.6-sol")
            context = AgentContext()
            agent.populate_context_post_run(context)

            self.assertEqual((30, 7), (context.n_input_tokens, context.n_output_tokens))
            self.assertEqual("partial", context.metadata["solvra_result"])
            self.assertEqual(1, context.metadata["solvra_failed_model_calls"])
            self.assertEqual(1, context.metadata["solvra_interrupted_operations"])
            self.assertEqual("unavailable", context.metadata["solvra_cache_usage"])

    def test_complete_json_is_authoritative(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "solvra.txt").write_text(
                'progress\n{\n  "turns": 2,\n  "stop_reason": "text",\n'
                '  "usage": {"input": 40, "output": 8},\n  "cost_usd": 0.5\n}\n'
            )
            agent = Solvra(logs_dir=root, model_name="chatgpt/gpt-5.6-sol")
            context = AgentContext()
            agent.populate_context_post_run(context)

            self.assertEqual((40, 8), (context.n_input_tokens, context.n_output_tokens))
            self.assertEqual(0.5, context.cost_usd)
            self.assertEqual("complete", context.metadata["solvra_result"])


if __name__ == "__main__":
    unittest.main()
