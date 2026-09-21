cd "$WORK"
python3 -c 'import json;d=json.load(open("answers.json"));assert d=={"prime_1000":7919,"digit_sum":1366,"sundays":171},d' || fail "wrong answers.json"
tool_used code_run || fail "code_run not used"
