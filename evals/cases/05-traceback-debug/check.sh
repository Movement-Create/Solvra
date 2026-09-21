cd "$WORK"
out=$(python3 -m invoicer.cli 2>&1) || fail "cli still crashes: $out"
echo "$out" | grep -q "TAX 2.55 USD" || fail "wrong tax output: $out"
python3 - <<'PY' || fail "defaults mutated or nested merge wrong"
import json, tempfile, os, sys
sys.path.insert(0, ".")
from invoicer import config
d = tempfile.mkdtemp(); p = os.path.join(d, "c.json")
json.dump({"tax": {"inclusive": True}}, open(p, "w"))
c = config.load(p)
assert c["tax"] == {"rate": 0.2, "inclusive": True}, c
assert config.DEFAULTS["tax"]["inclusive"] is False
c2 = config.load("/nonexistent.json"); c2["tax"]["rate"] = 9
assert config.DEFAULTS["tax"]["rate"] == 0.2, "DEFAULTS shared/mutated"
PY
[ -f tests/test_config.py ] || fail "no test added"
python3 -m unittest discover -s tests 2>&1 | tail -1 | grep -q OK || fail "tests fail"
