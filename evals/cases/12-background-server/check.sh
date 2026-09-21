cd "$WORK"
python3 -c 'import json;assert json.load(open("health.json"))=={"status":"ok","version":"1.4.2"}' || fail "health.json wrong"
if curl -s --max-time 2 http://127.0.0.1:18765/health >/dev/null; then fail "server still running"; fi
