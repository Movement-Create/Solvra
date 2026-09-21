cd "$WORK"
python3 - <<'PY' || fail "loc.json wrong: $(cat loc.json 2>/dev/null)"
import json, os, collections
exp = collections.Counter()
for root, _, files in os.walk("assets"):
    for f in files:
        ext = os.path.splitext(f)[1]
        exp[ext] += sum(1 for l in open(os.path.join(root, f)) if l.strip())
got = json.load(open("loc.json"))
assert {k: v for k, v in got.items() if v} == dict(exp), (got, dict(exp))
PY
python3 -c '
import sys; sys.path.insert(0,"src"); import calc
assert calc.percent_change(50,75)==50.0
try: calc.percent_change(0,5); raise SystemExit(1)
except ValueError: pass' || fail "percent_change not fixed"
[ -f tests/test_calc.py ] || fail "no test file"
tool_used agent || fail "agent tool not used"
tool_used todo || fail "todo not used"
