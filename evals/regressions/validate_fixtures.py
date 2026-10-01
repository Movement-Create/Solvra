#!/usr/bin/env python3
"""Fast structural validation for the independent regression definitions."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parent


def load(name: str):
    return json.loads((ROOT / name).read_text())


def main() -> None:
    manifest = load("manifest.json")
    assert manifest["schema_version"] == 1
    regressions = {item["id"]: item for item in manifest["regressions"]}
    assert set(regressions) == {
        "gpt2-codegolf",
        "filter-js-from-html",
        "torch-tensor-parallelism",
        "sanitize-git-repo",
    }
    for item in regressions.values():
        checks = item["independent_checks"]
        assert checks and len(checks) == len(set(checks))

    tensor = regressions["torch-tensor-parallelism"]
    assert tensor["world_sizes"] == [1, 2, 4]
    assert tensor["bias_values"] == [False, True]
    assert "row_already_sharded_input" in tensor["input_contracts"]

    browser = load("browser_cases.json")
    ids = [case["id"] for case in browser]
    assert len(ids) == len(set(ids))
    assert any(case["benign"] for case in browser)
    assert any(not case["benign"] for case in browser)
    assert all(case["html"] for case in browser)

    exact = regressions["sanitize-git-repo"]["independent_checks"]
    assert "bytes_outside_replacement_spans_unchanged" in exact
    deadline = regressions["gpt2-codegolf"]["independent_checks"]
    assert "runtime_under_90_seconds" in deadline
    assert "interrupted_usage_retained" in deadline
    print("4 regression definitions and browser corpus: valid")


if __name__ == "__main__":
    main()
